using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Carriers;

/// <summary>
/// Stands in for a prior-insurance bureau lookup.
/// </summary>
/// <remarks>
/// <para>
/// <b>POC behaviour, not a production design.</b> A real implementation would call
/// an external verification service over HTTP with credentials from a secret store,
/// wrapped in retry and timeout policies.
/// </para>
/// <para>
/// It exists to justify the asynchronous architecture. The delay is 250–750 ms —
/// small enough to demo, large enough that doing it inside the POST would be
/// indefensible once multiplied by the five-carrier fan-out that follows.
/// </para>
/// <para>
/// Submitting the customer name <c>FAIL-DEPENDENCY</c> throws
/// <see cref="DependencyUnavailableException"/>, which drives the retry and
/// dead-letter demonstration.
/// </para>
/// </remarks>
public sealed class SimulatedPriorInsuranceVerifier(
    ILogger<SimulatedPriorInsuranceVerifier> logger) : IPriorInsuranceVerifier
{
    /// <summary>Customer name that triggers a simulated dependency outage.</summary>
    public const string FailureTriggerCustomerName = "FAIL-DEPENDENCY";

    private const int MinimumDelayMs = 250;
    private const int MaximumDelayMs = 750;

    public async Task<PriorInsuranceVerification> VerifyAsync(Quote quote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var stopwatch = Stopwatch.StartNew();

        // Deterministic delay derived from the quote id: reproducible, and awaited
        // rather than slept so no thread is blocked.
        var delayMs = MinimumDelayMs + (Math.Abs(quote.Id.GetHashCode()) % (MaximumDelayMs - MinimumDelayMs));
        await Task.Delay(delayMs, ct);

        if (quote.CustomerName.Equals(FailureTriggerCustomerName, StringComparison.OrdinalIgnoreCase))
        {
            stopwatch.Stop();

            logger.LogWarning(
                "Prior-insurance verification dependency is unavailable. Operation={Operation} " +
                "QuoteId={QuoteId} QuoteNumber={QuoteNumber} CorrelationId={CorrelationId} " +
                "ElapsedMs={ElapsedMs} Result={Result}",
                "VerifyPriorInsurance", quote.Id, quote.QuoteNumber, quote.CorrelationId,
                stopwatch.ElapsedMilliseconds, "DependencyUnavailable");

            throw new DependencyUnavailableException(
                "The prior-insurance verification service is temporarily unavailable. " +
                "This message will be retried.");
        }

        stopwatch.Stop();

        // Deterministic result: the bureau corroborates the declaration. Returning
        // something derived from input keeps the whole demo reproducible.
        return new PriorInsuranceVerification
        {
            Verified = quote.PriorInsuranceMonths > 0,
            VerifiedMonths = quote.PriorInsuranceMonths,
            ReferenceId = $"VER-{quote.Id.ToString("N")[..12].ToUpperInvariant()}",
            ElapsedMs = (int)stopwatch.ElapsedMilliseconds
        };
    }
}

/// <summary>
/// Stands in for a card/ACH payment gateway.
/// </summary>
/// <remarks>
/// Returns a synthetic transaction reference and nothing else. There is no card
/// number, no token, no merchant credential and no network call anywhere in this
/// class — which is exactly why this POC has no PCI DSS surface. A real
/// integration would tokenise on the client and never let raw card data reach this
/// service at all.
/// </remarks>
public sealed class SimulatedPaymentGateway(ILogger<SimulatedPaymentGateway> logger) : IPaymentGateway
{
    public async Task<string> AuthorizeAsync(decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        // Simulated authorisation round trip.
        await Task.Delay(120, ct);

        // Derived from the idempotency key so a replayed authorisation of the same
        // intent yields the same reference.
        var reference = $"TXN-SIM-{Math.Abs(idempotencyKey.GetHashCode()):X8}";

        // The amount and reference are safe to log. The idempotency key is not
        // logged: it is client-chosen and could contain anything.
        logger.LogInformation(
            "Simulated payment authorised. Operation={Operation} Amount={Amount} " +
            "ExternalTransactionId={ExternalTransactionId} Result={Result}",
            "AuthorizePayment", amount, reference, "Approved");

        return reference;
    }
}
