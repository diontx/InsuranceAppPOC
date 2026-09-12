using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Policies;
using PolicyFlow.Domain.Common;

namespace PolicyFlow.Application.Payments;

/// <summary>Request to take a premium payment on a policy.</summary>
public sealed record SubmitPaymentCommand
{
    public required Guid PolicyId { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>
    /// Value of the <c>Idempotency-Key</c> request header. Required. The client
    /// generates one key per payment <i>intent</i> and reuses it on every retry of
    /// that intent, which is what makes a retry safe.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}

/// <summary>
/// Takes a payment, exactly once per idempotency key.
/// </summary>
/// <remarks>
/// <para>
/// Duplicate payment requests are not hypothetical: users double-click, mobile
/// networks retry, load balancers replay, and a client that times out cannot tell
/// a lost request from a lost response. Disabling a button in the UI does not help
/// — the request has already left the browser, and the next client will not have
/// that button at all.
/// </para>
/// <para>The guarantee has three layers, in order of strength:</para>
/// <list type="number">
///   <item><b>UNIQUE index</b> on <c>(PolicyId, IdempotencyKey)</c> in SQL Server.
///         This is the actual guarantee. It holds even when two requests execute
///         concurrently on different servers.</item>
///   <item><b>Pre-read</b> for the existing key. An optimisation that turns the
///         common retry into a fast 200 instead of a constraint violation.</item>
///   <item><b>Catch on insert</b>. When the pre-read loses a race, the unique
///         violation is translated back into a replay of the original payment,
///         so the client still sees the same successful result.</item>
/// </list>
/// <para>
/// The gateway is called only after the pre-read misses, so a retry never
/// re-authorises. Only the amount, status and a synthetic reference are stored: no
/// card data or credentials exist anywhere in this flow.
/// </para>
/// </remarks>
public sealed class SubmitPaymentHandler(
    IPolicyRepository policies,
    IPaymentRepository payments,
    IUnitOfWork unitOfWork,
    IPaymentGateway gateway,
    IClock clock,
    ICorrelationContext correlation,
    ILogger<SubmitPaymentHandler> logger)
{
    public async Task<PaymentView> HandleAsync(SubmitPaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            throw new MissingIdempotencyKeyException();
        }

        var key = command.IdempotencyKey.Trim();
        var correlationId = correlation.CorrelationId;

        var policy = await policies.GetWithPaymentsAsync(command.PolicyId, ct)
            ?? throw new PolicyNotFoundException(command.PolicyId);

        // --- Layer 2: fast path for a retried intent. No gateway call, no insert.
        var existing = await payments.FindByIdempotencyKeyAsync(policy.Id, key, ct);
        if (existing is not null)
        {
            logger.LogInformation(
                "Payment request replayed from idempotency key. Operation={Operation} PolicyId={PolicyId} " +
                "PolicyNumber={PolicyNumber} PaymentId={PaymentId} ExternalTransactionId={ExternalTransactionId} " +
                "Amount={Amount} CorrelationId={CorrelationId} Result={Result}",
                "SubmitPayment", policy.Id, policy.PolicyNumber, existing.Id, existing.ExternalTransactionId,
                existing.Amount, correlationId, "Replayed");

            return PaymentView.From(existing, wasReplayed: true);
        }

        // Authorise only on the genuinely new path.
        var externalTransactionId = await gateway.AuthorizeAsync(command.Amount, key, ct);

        try
        {
            var payment = await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                var recorded = policy.RecordPayment(
                    command.Amount, key, externalTransactionId, correlationId, clock.UtcNow);

                await payments.AddAsync(recorded, token);
                await unitOfWork.SaveChangesAsync(token);
                return recorded;
            }, ct);

            logger.LogInformation(
                "Payment recorded. Operation={Operation} PolicyId={PolicyId} PolicyNumber={PolicyNumber} " +
                "PaymentId={PaymentId} ExternalTransactionId={ExternalTransactionId} Amount={Amount} " +
                "CorrelationId={CorrelationId} Result={Result}",
                "SubmitPayment", policy.Id, policy.PolicyNumber, payment.Id, payment.ExternalTransactionId,
                payment.Amount, correlationId, "Approved");

            return PaymentView.From(payment);
        }
        catch (DuplicateIdempotencyKeyException)
        {
            // --- Layer 3: the pre-read lost a race. The UNIQUE index caught the
            //     second insert; return the payment that actually won.
            var winner = await payments.FindByIdempotencyKeyAsync(policy.Id, key, ct);
            if (winner is null)
            {
                // A unique violation with no matching row means the constraint that
                // fired was a different one. Do not paper over it.
                throw;
            }

            logger.LogWarning(
                "Concurrent payment collapsed onto the original transaction. Operation={Operation} " +
                "PolicyId={PolicyId} PolicyNumber={PolicyNumber} PaymentId={PaymentId} " +
                "CorrelationId={CorrelationId} Result={Result}",
                "SubmitPayment", policy.Id, policy.PolicyNumber, winner.Id, correlationId, "RaceCollapsed");

            return PaymentView.From(winner, wasReplayed: true);
        }
    }
}

/// <summary>
/// The <c>Idempotency-Key</c> header was absent. Maps to HTTP 400: an unkeyed
/// payment cannot be made safe, so the API refuses rather than guessing.
/// </summary>
public sealed class MissingIdempotencyKeyException()
    : Exception("An Idempotency-Key header is required to submit a payment.");

/// <summary>
/// Signals that the unique idempotency index rejected an insert. Raised by the
/// infrastructure layer so the application layer never inspects a provider-specific
/// SQL error number.
/// </summary>
public sealed class DuplicateIdempotencyKeyException(string idempotencyKey, Exception inner)
    : Exception($"A payment already exists for idempotency key '{idempotencyKey}'.", inner)
{
    public string IdempotencyKey { get; } = idempotencyKey;
}
