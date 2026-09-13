using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Quotes;

/// <summary>What the carrier panel returned for one quote.</summary>
public sealed record CarrierPanelResult
{
    /// <summary>Every priced option across the panel.</summary>
    public required IReadOnlyList<CarrierOffer> Offers { get; init; }

    /// <summary>Carriers that returned nothing, with the reason.</summary>
    public required IReadOnlyList<CarrierDeclination> Declinations { get; init; }

    /// <summary>Wall-clock time of the concurrent fan-out.</summary>
    public required long FanOutMs { get; init; }
}

/// <summary>
/// Shops a quote across the carrier panel and maintains its comparison read model.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="UnderwriteQuoteHandler"/> when underwriter approval
/// needed the same fan-out. The alternative — a second copy of the fan-out in the
/// review handler — is exactly the "same rule existing twice" failure this codebase
/// is organised to prevent. Two callers, one implementation:
/// </para>
/// <list type="bullet">
///   <item>the worker, for automated underwriting;</item>
///   <item>an underwriter approving a referral.</item>
/// </list>
/// </remarks>
public sealed class CarrierMarketplace(
    ICarrierCatalog catalog,
    ICarrierRatingGateway carrierGateway,
    IQuoteComparisonStore comparisonStore,
    IClock clock,
    ILogger<CarrierMarketplace> logger)
{
    /// <summary>Requests offers from every active carrier concurrently.</summary>
    public async Task<CarrierPanelResult> ShopAsync(Quote quote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var carriers = await catalog.GetActiveCarriersAsync(ct);
        var fanOut = Stopwatch.StartNew();

        // Concurrent, not sequential: five carriers averaging 400ms each would take
        // two seconds in series. Each gateway isolates its own failure, so one slow
        // or broken carrier cannot take down the panel.
        var carrierResults = await Task.WhenAll(
            carriers.Select(carrier => carrierGateway.RequestOffersAsync(quote, carrier, ct)));

        fanOut.Stop();

        foreach (var carrierResult in carrierResults)
        {
            logger.LogInformation(
                "Carrier rated. Operation={Operation} QuoteId={QuoteId} CarrierId={CarrierId} " +
                "OfferCount={OfferCount} Failed={Failed} Reason={Reason} CorrelationId={CorrelationId} ElapsedMs={ElapsedMs}",
                "RateCarrier", quote.Id, carrierResult.CarrierId, carrierResult.Offers.Count,
                carrierResult.Failed, carrierResult.DeclineReason, quote.CorrelationId, carrierResult.ElapsedMs);
        }

        return new CarrierPanelResult
        {
            Offers = carrierResults.SelectMany(r => r.Offers).ToList(),
            Declinations = carrierResults
                .Where(r => r.Offers.Count == 0)
                .Select(r => new CarrierDeclination
                {
                    CarrierId = r.CarrierId,
                    CarrierName = r.CarrierName,
                    Reason = r.DeclineReason ?? "No offer returned.",
                    WasUnavailable = r.Failed
                })
                .ToList(),
            FanOutMs = fanOut.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Writes the denormalised comparison document to the NoSQL store.
    /// </summary>
    /// <remarks>
    /// Failure here is logged but never rethrown. The relational record is the
    /// source of truth and has already committed; losing a read-model refresh must
    /// not cause the Service Bus message to be retried and the quote re-underwritten,
    /// nor turn an underwriter's committed approval into an error response.
    /// </remarks>
    public async Task WriteComparisonDocumentAsync(
        Quote quote,
        decimal? baselineAnnualPremium,
        IReadOnlyList<CarrierOffer> offers,
        IReadOnlyList<CarrierDeclination> declinations,
        long elapsedMs,
        CancellationToken ct = default)
    {
        try
        {
            var carriers = await catalog.GetActiveCarriersAsync(ct);
            var byId = carriers.ToDictionary(c => c.CarrierId, StringComparer.OrdinalIgnoreCase);
            var bestPremium = offers.Count > 0 ? offers.Min(o => o.AnnualPremium) : (decimal?)null;

            var document = new QuoteComparisonDocument
            {
                QuoteId = quote.Id,
                QuoteNumber = quote.QuoteNumber,
                CustomerName = quote.CustomerName,
                State = quote.State,
                VehicleDescription = $"{quote.VehicleYear} {quote.VehicleMake} {quote.VehicleModel}",
                Status = quote.Status.ToString(),
                BaselineAnnualPremium = baselineAnnualPremium,
                Offers = offers
                    .OrderBy(o => o.AnnualPremium)
                    .Select(o => new ComparisonOffer
                    {
                        OfferId = o.Id,
                        CarrierId = o.CarrierId,
                        CarrierName = o.CarrierName,
                        Tagline = byId.TryGetValue(o.CarrierId, out var profile) ? profile.Tagline : string.Empty,
                        BrandColor = byId.TryGetValue(o.CarrierId, out var brand) ? brand.BrandColor : "#1F3C88",
                        FinancialStrengthRating = o.FinancialStrengthRating,
                        CoverageTier = o.CoverageTier.ToString(),
                        AnnualPremium = o.AnnualPremium,
                        MonthlyPremium = o.MonthlyPremium,
                        Deductible = o.Deductible,
                        LimitsSummary =
                            $"{o.BodilyInjuryPerPerson / 1000}/{o.BodilyInjuryPerAccident / 1000}/{o.PropertyDamage / 1000}",
                        BodilyInjuryPerPerson = o.BodilyInjuryPerPerson,
                        BodilyInjuryPerAccident = o.BodilyInjuryPerAccident,
                        PropertyDamage = o.PropertyDamage,
                        IncludesRoadside = o.IncludesRoadside,
                        IncludesRentalReimbursement = o.IncludesRentalReimbursement,
                        DiscountAmount = o.DiscountAmount,
                        AppliedDiscounts = SplitDiscounts(o.AppliedDiscounts),
                        RatingTrace = SplitTrace(o.RatingTrace),
                        IsBestPrice = bestPremium.HasValue && o.AnnualPremium == bestPremium.Value
                    })
                    .ToList(),
                Declinations = declinations,
                GeneratedUtc = clock.UtcNow,
                CorrelationId = quote.CorrelationId,
                TotalRatingMs = (int)elapsedMs
            };

            await comparisonStore.UpsertAsync(document, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Comparison read model was not updated; relational state is authoritative. " +
                "Operation={Operation} QuoteId={QuoteId} CorrelationId={CorrelationId} Result={Result}",
                "WriteComparisonDocument", quote.Id, quote.CorrelationId, "Failed");
        }
    }

    private static List<string> SplitDiscounts(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> SplitTrace(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .ToList();
}
