using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Payments;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Policies;

public enum PolicyStatus
{
    Active = 0,
    Cancelled = 1,
    Expired = 2
}

/// <summary>
/// A bound policy. Created only from a <see cref="QuoteStatus.Rated"/> quote and a
/// specific <see cref="CarrierOffer"/>, so the premium the customer was shown is
/// the premium that binds. The quote-to-policy relationship is unique in SQL,
/// which is the database-level guarantee behind
/// <see cref="BusinessConflictException"/> on a duplicate bind attempt.
/// </summary>
public class Policy
{
    private Policy() { }

    private readonly List<Payment> _payments = [];

    public Guid Id { get; private set; }

    /// <summary>Human-facing identifier, e.g. <c>POL-2026-000042</c>. Unique.</summary>
    public string PolicyNumber { get; private set; } = null!;

    public Guid QuoteId { get; private set; }

    /// <summary>The offer the customer selected. Pins carrier, tier and premium.</summary>
    public Guid CarrierOfferId { get; private set; }

    public string CarrierId { get; private set; } = null!;

    public string CarrierName { get; private set; } = null!;

    public CoverageTier CoverageTier { get; private set; }

    public string InsuredName { get; private set; } = null!;

    public string State { get; private set; } = null!;

    public DateOnly EffectiveDate { get; private set; }

    public DateOnly ExpirationDate { get; private set; }

    public PolicyStatus Status { get; private set; }

    public decimal AnnualPremium { get; private set; }

    public decimal MonthlyPremium { get; private set; }

    public decimal Deductible { get; private set; }

    public string CorrelationId { get; private set; } = null!;

    public DateTime CreatedUtc { get; private set; }

    public DateTime UpdatedUtc { get; private set; }

    public byte[]? RowVersion { get; private set; }

    public Quote? Quote { get; private set; }

    public IReadOnlyCollection<Payment> Payments => _payments.AsReadOnly();

    /// <summary>Sum of approved payments recorded against the policy.</summary>
    public decimal TotalPaid => _payments
        .Where(p => p.Status == PaymentStatus.Approved)
        .Sum(p => p.Amount);

    /// <summary>
    /// Binds a rated quote to a twelve-month policy term on the selected offer.
    /// Throws <see cref="DomainException"/> when the quote is not rated or the
    /// offer does not belong to it.
    /// </summary>
    public static Policy CreateFromQuote(
        Quote quote,
        CarrierOffer offer,
        string policyNumber,
        DateOnly effectiveDate,
        string correlationId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(offer);

        if (quote.Status != QuoteStatus.Rated)
        {
            throw new DomainException(
                $"Quote {quote.QuoteNumber} is {quote.Status}. Only a Rated quote can be converted to a policy.");
        }

        if (offer.QuoteId != quote.Id)
        {
            throw new DomainException($"Offer {offer.Id} does not belong to quote {quote.QuoteNumber}.");
        }

        if (string.IsNullOrWhiteSpace(policyNumber))
        {
            throw new DomainException("Policy number is required.");
        }

        return new Policy
        {
            Id = Guid.NewGuid(),
            PolicyNumber = policyNumber,
            QuoteId = quote.Id,
            CarrierOfferId = offer.Id,
            CarrierId = offer.CarrierId,
            CarrierName = offer.CarrierName,
            CoverageTier = offer.CoverageTier,
            InsuredName = quote.CustomerName,
            State = quote.State,
            EffectiveDate = effectiveDate,
            ExpirationDate = effectiveDate.AddYears(1),
            Status = PolicyStatus.Active,
            AnnualPremium = offer.AnnualPremium,
            MonthlyPremium = offer.MonthlyPremium,
            Deductible = offer.Deductible,
            CorrelationId = correlationId,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };
    }

    /// <summary>Cancels an active policy. Cancelling twice is rejected.</summary>
    public void Cancel(DateTime nowUtc)
    {
        if (Status != PolicyStatus.Active)
        {
            throw new DomainException($"Policy {PolicyNumber} is {Status} and cannot be cancelled.");
        }

        Status = PolicyStatus.Cancelled;
        UpdatedUtc = nowUtc;
    }

    /// <summary>
    /// Records a payment against the policy. Idempotency is enforced one layer up
    /// (a unique index on the key) because the guarantee must survive a process
    /// crash between two concurrent requests, which in-memory state cannot do.
    /// </summary>
    public Payment RecordPayment(
        decimal amount,
        string idempotencyKey,
        string externalTransactionId,
        string correlationId,
        DateTime nowUtc)
    {
        if (Status != PolicyStatus.Active)
        {
            throw new DomainException($"Policy {PolicyNumber} is {Status}; payments are only accepted on an Active policy.");
        }

        var payment = Payment.Approve(
            Id, amount, idempotencyKey, externalTransactionId, correlationId, nowUtc);

        _payments.Add(payment);
        UpdatedUtc = nowUtc;
        return payment;
    }
}
