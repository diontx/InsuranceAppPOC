using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Policies;

namespace PolicyFlow.Application.Policies;

/// <summary>Request to bind a rated quote into a policy.</summary>
public sealed record CreatePolicyFromQuoteCommand
{
    public required Guid QuoteId { get; init; }

    /// <summary>Chosen carrier offer. Null binds the cheapest offer in the panel.</summary>
    public Guid? OfferId { get; init; }

    /// <summary>Requested effective date. Null means tomorrow.</summary>
    public DateOnly? EffectiveDate { get; init; }
}

public sealed record PolicyView
{
    public required Guid PolicyId { get; init; }
    public required string PolicyNumber { get; init; }
    public required Guid QuoteId { get; init; }
    public required string QuoteNumber { get; init; }
    public required string Status { get; init; }
    public required string InsuredName { get; init; }
    public required string State { get; init; }
    public required string CarrierId { get; init; }
    public required string CarrierName { get; init; }
    public required string CoverageTier { get; init; }
    public required decimal AnnualPremium { get; init; }
    public required decimal MonthlyPremium { get; init; }
    public required decimal Deductible { get; init; }
    public required decimal TotalPaid { get; init; }
    public required decimal BalanceDue { get; init; }
    public required DateOnly EffectiveDate { get; init; }
    public required DateOnly ExpirationDate { get; init; }
    public required IReadOnlyList<PaymentView> Payments { get; init; }
    public required string CorrelationId { get; init; }
    public required DateTime CreatedUtc { get; init; }

    public static PolicyView From(Policy policy, string quoteNumber) => new()
    {
        PolicyId = policy.Id,
        PolicyNumber = policy.PolicyNumber,
        QuoteId = policy.QuoteId,
        QuoteNumber = quoteNumber,
        Status = policy.Status.ToString(),
        InsuredName = policy.InsuredName,
        State = policy.State,
        CarrierId = policy.CarrierId,
        CarrierName = policy.CarrierName,
        CoverageTier = policy.CoverageTier.ToString(),
        AnnualPremium = policy.AnnualPremium,
        MonthlyPremium = policy.MonthlyPremium,
        Deductible = policy.Deductible,
        TotalPaid = policy.TotalPaid,
        BalanceDue = decimal.Round(policy.AnnualPremium - policy.TotalPaid, 2),
        EffectiveDate = policy.EffectiveDate,
        ExpirationDate = policy.ExpirationDate,
        Payments = policy.Payments
            .OrderBy(p => p.CreatedUtc)
            .Select(p => PaymentView.From(p))
            .ToList(),
        CorrelationId = policy.CorrelationId,
        CreatedUtc = policy.CreatedUtc
    };
}

public sealed record PaymentView
{
    public required Guid PaymentId { get; init; }
    public required decimal Amount { get; init; }
    public required string Status { get; init; }
    public required string ExternalTransactionId { get; init; }
    public required DateTime CreatedUtc { get; init; }

    /// <summary>
    /// True when this response replayed an existing payment rather than creating
    /// one. The demo asserts on this to prove idempotency.
    /// </summary>
    public bool WasReplayed { get; init; }

    public static PaymentView From(Domain.Payments.Payment payment, bool wasReplayed = false) => new()
    {
        PaymentId = payment.Id,
        Amount = payment.Amount,
        Status = payment.Status.ToString(),
        ExternalTransactionId = payment.ExternalTransactionId,
        CreatedUtc = payment.CreatedUtc,
        WasReplayed = wasReplayed
    };
}

/// <summary>
/// Binds a rated quote into a policy.
/// </summary>
/// <remarks>
/// Two guards stop a duplicate bind: a pre-read that returns a clean 409, and a
/// UNIQUE index on <c>Policy.QuoteId</c>. The read alone is not enough — two
/// concurrent requests can both pass it — so the index is the real guarantee and
/// the read only exists to produce a friendlier error in the common case.
/// </remarks>
public sealed class CreatePolicyFromQuoteHandler(
    IQuoteRepository quotes,
    IPolicyRepository policies,
    IUnitOfWork unitOfWork,
    IClock clock,
    ICorrelationContext correlation,
    ILogger<CreatePolicyFromQuoteHandler> logger)
{
    public async Task<PolicyView> HandleAsync(CreatePolicyFromQuoteCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var quote = await quotes.GetWithOffersAsync(command.QuoteId, ct)
            ?? throw new QuoteNotFoundException(command.QuoteId);

        var existing = await policies.GetByQuoteIdAsync(command.QuoteId, ct);
        if (existing is not null)
        {
            logger.LogWarning(
                "Duplicate policy binding rejected. Operation={Operation} QuoteId={QuoteId} " +
                "ExistingPolicyNumber={PolicyNumber} CorrelationId={CorrelationId} Result={Result}",
                "CreatePolicy", command.QuoteId, existing.PolicyNumber, correlation.CorrelationId, "Conflict");

            throw new BusinessConflictException(
                $"Quote {quote.QuoteNumber} is already bound to policy {existing.PolicyNumber}.");
        }

        // Throws DomainException when the quote is not Rated or the offer is foreign.
        var offer = quote.SelectOfferForBinding(command.OfferId);
        var now = clock.UtcNow;
        var effectiveDate = command.EffectiveDate ?? DateOnly.FromDateTime(now.Date).AddDays(1);

        var policy = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var policyNumber = await policies.NextPolicyNumberAsync(now.Year, token);

            var created = Policy.CreateFromQuote(
                quote, offer, policyNumber, effectiveDate, correlation.CorrelationId, now);

            await policies.AddAsync(created, token);
            await unitOfWork.SaveChangesAsync(token);
            return created;
        }, ct);

        logger.LogInformation(
            "Policy bound. Operation={Operation} PolicyId={PolicyId} PolicyNumber={PolicyNumber} " +
            "QuoteId={QuoteId} QuoteNumber={QuoteNumber} CarrierId={CarrierId} CoverageTier={CoverageTier} " +
            "AnnualPremium={AnnualPremium} CorrelationId={CorrelationId} Result={Result}",
            "CreatePolicy", policy.Id, policy.PolicyNumber, quote.Id, quote.QuoteNumber,
            policy.CarrierId, policy.CoverageTier, policy.AnnualPremium,
            correlation.CorrelationId, "Created");

        return PolicyView.From(policy, quote.QuoteNumber);
    }
}

/// <summary>Quote id does not exist. Maps to HTTP 404.</summary>
public sealed class QuoteNotFoundException(Guid quoteId)
    : Exception($"Quote {quoteId} was not found.")
{
    public Guid QuoteId { get; } = quoteId;
}

/// <summary>Policy id does not exist. Maps to HTTP 404.</summary>
public sealed class PolicyNotFoundException(Guid policyId)
    : Exception($"Policy {policyId} was not found.")
{
    public Guid PolicyId { get; } = policyId;
}
