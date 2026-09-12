using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Quotes;

/// <summary>
/// Reads the carrier comparison for a rated quote.
/// </summary>
/// <remarks>
/// <para>
/// Serves the NoSQL comparison document when it exists: the mobile comparison
/// screen needs carrier branding, coverage limits, discounts and a rating trace
/// for up to fifteen offers, which is one document read instead of a relational
/// join across quote, offer and carrier tables.
/// </para>
/// <para>
/// When the document is missing — Mongo was down during underwriting, or the read
/// model has not caught up — it falls back to projecting from the relational rows.
/// Slower, but correct: the read model is a cache, not the source of truth.
/// </para>
/// </remarks>
public sealed class GetQuoteOffersHandler(
    IQuoteComparisonStore comparisonStore,
    IQuoteRepository quotes,
    ICarrierCatalog catalog,
    ILogger<GetQuoteOffersHandler> logger)
{
    public async Task<QuoteComparisonDocument?> HandleAsync(Guid quoteId, CancellationToken ct = default)
    {
        var document = await comparisonStore.GetByQuoteIdAsync(quoteId, ct);
        if (document is not null)
        {
            return document;
        }

        var quote = await quotes.GetWithOffersAsync(quoteId, ct);
        if (quote is null)
        {
            return null;
        }

        logger.LogWarning(
            "Comparison document missing; projecting from SQL. Operation={Operation} QuoteId={QuoteId} " +
            "QuoteNumber={QuoteNumber} CorrelationId={CorrelationId} Result={Result}",
            "GetQuoteOffers", quote.Id, quote.QuoteNumber, quote.CorrelationId, "RelationalFallback");

        return await ProjectFromRelationalAsync(quote, ct);
    }

    /// <summary>Mobile lookup path: quote number plus surname, then the comparison.</summary>
    public async Task<QuoteComparisonDocument?> LookupAsync(
        string quoteNumber,
        string lastName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(quoteNumber) || string.IsNullOrWhiteSpace(lastName))
        {
            return null;
        }

        var quote = await quotes.FindForSelfServiceLookupAsync(quoteNumber.Trim(), lastName.Trim(), ct);
        return quote is null ? null : await HandleAsync(quote.Id, ct);
    }

    private async Task<QuoteComparisonDocument> ProjectFromRelationalAsync(Quote quote, CancellationToken ct)
    {
        var carriers = await catalog.GetActiveCarriersAsync(ct);
        var byId = carriers.ToDictionary(c => c.CarrierId, StringComparer.OrdinalIgnoreCase);
        var offers = quote.Offers.OrderBy(o => o.AnnualPremium).ToList();
        var bestPremium = offers.Count > 0 ? offers.Min(o => o.AnnualPremium) : (decimal?)null;
        var quotingCarriers = offers.Select(o => o.CarrierId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new QuoteComparisonDocument
        {
            QuoteId = quote.Id,
            QuoteNumber = quote.QuoteNumber,
            CustomerName = quote.CustomerName,
            State = quote.State,
            VehicleDescription = $"{quote.VehicleYear} {quote.VehicleMake} {quote.VehicleModel}",
            Status = quote.Status.ToString(),
            BaselineAnnualPremium = null,
            Offers = offers.Select(o => new ComparisonOffer
            {
                OfferId = o.Id,
                CarrierId = o.CarrierId,
                CarrierName = o.CarrierName,
                Tagline = byId.TryGetValue(o.CarrierId, out var p) ? p.Tagline : string.Empty,
                BrandColor = byId.TryGetValue(o.CarrierId, out var b) ? b.BrandColor : "#1F3C88",
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
                AppliedDiscounts = string.IsNullOrWhiteSpace(o.AppliedDiscounts)
                    ? []
                    : o.AppliedDiscounts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                RatingTrace = string.IsNullOrWhiteSpace(o.RatingTrace)
                    ? []
                    : o.RatingTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.TrimEnd('\r')).ToList(),
                IsBestPrice = bestPremium.HasValue && o.AnnualPremium == bestPremium.Value
            }).ToList(),
            // Any catalog carrier absent from the offer set had no appetite. The
            // specific reason lives only in the read model, so say so plainly
            // rather than inventing one.
            Declinations = carriers
                .Where(c => !quotingCarriers.Contains(c.CarrierId))
                .Select(c => new CarrierDeclination
                {
                    CarrierId = c.CarrierId,
                    CarrierName = c.DisplayName,
                    Reason = "No offer was returned for this risk.",
                    WasUnavailable = false
                })
                .ToList(),
            GeneratedUtc = quote.UpdatedUtc,
            CorrelationId = quote.CorrelationId,
            TotalRatingMs = 0
        };
    }
}
