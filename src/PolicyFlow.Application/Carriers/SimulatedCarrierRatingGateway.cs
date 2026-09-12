using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Domain.Rating;

namespace PolicyFlow.Application.Carriers;

/// <summary>
/// Stands in for five real carrier rating APIs.
/// </summary>
/// <remarks>
/// <para>
/// <b>POC behaviour, not a production design.</b> A real integration would be an
/// HTTP or SOAP client per carrier, behind Polly retry and circuit-breaker
/// policies, with per-carrier credentials from a secret store. What is faithfully
/// modelled here is the <i>shape</i> of that dependency: it is remote, it is slow,
/// it fails independently, and it must never be called from inside a synchronous
/// HTTP request.
/// </para>
/// <para>
/// Latency is simulated with <c>Task.Delay</c>, never <c>Thread.Sleep</c>. A
/// blocking sleep would consume a thread-pool thread per carrier; with a five-way
/// fan-out under load that is how a service starves itself.
/// </para>
/// <para>
/// The premium itself is computed by <see cref="CarrierRatingService"/> in the
/// domain layer. This class only adds the transport simulation, so pricing stays
/// deterministic and unit-testable without any of this machinery.
/// </para>
/// </remarks>
public sealed class SimulatedCarrierRatingGateway(
    CarrierRatingService ratingService,
    IClock clock,
    ILogger<SimulatedCarrierRatingGateway> logger) : ICarrierRatingGateway
{
    /// <summary>
    /// Submitting this customer name makes every carrier gateway fail, so the demo
    /// can show retry and dead-letter behaviour on command.
    /// </summary>
    public const string FailureTriggerCustomerName = "FAIL-CARRIER";

    public async Task<CarrierQuoteResult> RequestOffersAsync(
        Quote quote,
        CarrierProfile carrier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(carrier);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Deterministic latency: derived from the quote and carrier, so the same
            // input always takes the same simulated time. Random jitter would make
            // demo timings irreproducible.
            await Task.Delay(SimulatedLatency(quote, carrier), ct);

            if (quote.CustomerName.Equals(FailureTriggerCustomerName, StringComparison.OrdinalIgnoreCase))
            {
                throw new DependencyUnavailableException(
                    $"Simulated transport failure calling {carrier.DisplayName}.");
            }

            if (!ratingService.IsInAppetite(quote, carrier, out var declineReason))
            {
                stopwatch.Stop();
                return CarrierQuoteResult.Declined(carrier, declineReason!, (int)stopwatch.ElapsedMilliseconds);
            }

            var offers = ratingService.RateCarrier(quote, carrier, clock.UtcNow);
            stopwatch.Stop();

            return offers.Count == 0
                ? CarrierQuoteResult.Declined(carrier, "Carrier returned no priced options.", (int)stopwatch.ElapsedMilliseconds)
                : CarrierQuoteResult.Quoted(carrier, offers, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Shutdown or a caller-side timeout. Let it propagate so the worker can
            // abandon the message rather than record a bogus declination.
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // One carrier failing must not fail the whole panel. The failure is
            // surfaced as an unavailable carrier so the UI can say "not available"
            // instead of silently showing a shorter list.
            logger.LogError(
                ex,
                "Carrier gateway call failed; panel continues without this carrier. " +
                "Operation={Operation} QuoteId={QuoteId} CarrierId={CarrierId} " +
                "CorrelationId={CorrelationId} ElapsedMs={ElapsedMs} Result={Result}",
                "RequestCarrierOffers", quote.Id, carrier.CarrierId, quote.CorrelationId,
                stopwatch.ElapsedMilliseconds, "Unavailable");

            return CarrierQuoteResult.Unavailable(
                carrier,
                $"{carrier.DisplayName} did not respond in time.",
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Picks a delay inside the carrier's configured band, derived from the quote id
    /// so it is stable per quote rather than random per call.
    /// </summary>
    private static TimeSpan SimulatedLatency(Quote quote, CarrierProfile carrier)
    {
        var span = Math.Max(1, carrier.SimulatedLatencyMsMax - carrier.SimulatedLatencyMsMin);
        var offset = Math.Abs(HashCode.Combine(quote.Id, carrier.CarrierId)) % span;
        return TimeSpan.FromMilliseconds(carrier.SimulatedLatencyMsMin + offset);
    }
}
