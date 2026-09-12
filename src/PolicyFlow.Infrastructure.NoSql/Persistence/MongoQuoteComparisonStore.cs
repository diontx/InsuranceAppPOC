using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Infrastructure.NoSql.Documents;

namespace PolicyFlow.Infrastructure.NoSql.Persistence;

/// <summary>
/// Reads and writes the denormalised comparison read model in MongoDB via EF Core.
/// </summary>
/// <remarks>
/// Writes are upserts keyed on <c>QuoteId</c>: re-underwriting a quote replaces its
/// document rather than accumulating duplicates.
/// </remarks>
public sealed class MongoQuoteComparisonStore(
    CarrierCatalogDbContext db,
    ILogger<MongoQuoteComparisonStore> logger) : IQuoteComparisonStore
{
    public async Task UpsertAsync(QuoteComparisonDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var existing = await db.QuoteComparisons
            .FirstOrDefaultAsync(d => d.QuoteId == document.QuoteId, ct);

        if (existing is null)
        {
            var created = new QuoteComparisonDoc { QuoteId = document.QuoteId };
            Apply(document, created);
            await db.QuoteComparisons.AddAsync(created, ct);
        }
        else
        {
            Apply(document, existing);
            db.QuoteComparisons.Update(existing);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Comparison read model written. Operation={Operation} QuoteId={QuoteId} " +
            "QuoteNumber={QuoteNumber} OfferCount={OfferCount} DeclinationCount={DeclinationCount} " +
            "CorrelationId={CorrelationId} Result={Result}",
            "UpsertQuoteComparison", document.QuoteId, document.QuoteNumber,
            document.Offers.Count, document.Declinations.Count, document.CorrelationId, "Written");
    }

    public async Task<QuoteComparisonDocument?> GetByQuoteIdAsync(Guid quoteId, CancellationToken ct = default)
    {
        try
        {
            var document = await db.QuoteComparisons
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.QuoteId == quoteId, ct);

            return document is null ? null : ToApplication(document);
        }
        catch (Exception ex)
        {
            // Returning null makes the caller fall back to projecting from SQL,
            // which is correct because this store is a cache.
            logger.LogError(
                ex,
                "Comparison read model lookup failed; caller will fall back to SQL. " +
                "Operation={Operation} QuoteId={QuoteId} Result={Result}",
                "GetQuoteComparison", quoteId, "Unavailable");

            return null;
        }
    }

    public async Task<QuoteComparisonDocument?> GetByQuoteNumberAsync(
        string quoteNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(quoteNumber))
        {
            return null;
        }

        try
        {
            var document = await db.QuoteComparisons
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.QuoteNumber == quoteNumber, ct);

            return document is null ? null : ToApplication(document);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Comparison read model lookup by quote number failed. Operation={Operation} " +
                "QuoteNumber={QuoteNumber} Result={Result}",
                "GetQuoteComparison", quoteNumber, "Unavailable");

            return null;
        }
    }

    private static void Apply(QuoteComparisonDocument source, QuoteComparisonDoc target)
    {
        target.QuoteId = source.QuoteId;
        target.QuoteNumber = source.QuoteNumber;
        target.CustomerName = source.CustomerName;
        target.State = source.State;
        target.VehicleDescription = source.VehicleDescription;
        target.Status = source.Status;
        target.BaselineAnnualPremium = source.BaselineAnnualPremium is null
            ? null
            : (double)source.BaselineAnnualPremium.Value;
        target.Offers = source.Offers.Select(o => new ComparisonOfferSubDocument
        {
            OfferId = o.OfferId,
            CarrierId = o.CarrierId,
            CarrierName = o.CarrierName,
            Tagline = o.Tagline,
            BrandColor = o.BrandColor,
            FinancialStrengthRating = o.FinancialStrengthRating,
            CoverageTier = o.CoverageTier,
            AnnualPremium = (double)o.AnnualPremium,
            MonthlyPremium = (double)o.MonthlyPremium,
            Deductible = (double)o.Deductible,
            LimitsSummary = o.LimitsSummary,
            BodilyInjuryPerPerson = o.BodilyInjuryPerPerson,
            BodilyInjuryPerAccident = o.BodilyInjuryPerAccident,
            PropertyDamage = o.PropertyDamage,
            IncludesRoadside = o.IncludesRoadside,
            IncludesRentalReimbursement = o.IncludesRentalReimbursement,
            DiscountAmount = (double)o.DiscountAmount,
            AppliedDiscounts = o.AppliedDiscounts.ToList(),
            RatingTrace = o.RatingTrace.ToList(),
            IsBestPrice = o.IsBestPrice
        }).ToList();
        target.Declinations = source.Declinations.Select(d => new DeclinationSubDocument
        {
            CarrierId = d.CarrierId,
            CarrierName = d.CarrierName,
            Reason = d.Reason,
            WasUnavailable = d.WasUnavailable
        }).ToList();
        target.GeneratedUtc = source.GeneratedUtc;
        target.CorrelationId = source.CorrelationId;
        target.TotalRatingMs = source.TotalRatingMs;
    }

    private static QuoteComparisonDocument ToApplication(QuoteComparisonDoc document) => new()
    {
        QuoteId = document.QuoteId,
        QuoteNumber = document.QuoteNumber,
        CustomerName = document.CustomerName,
        State = document.State,
        VehicleDescription = document.VehicleDescription,
        Status = document.Status,
        BaselineAnnualPremium = document.BaselineAnnualPremium is null
            ? null
            // Round on the way out: a double round-trip of 1482.50 can surface as
            // 1482.4999999999998, which must never reach a price on screen.
            : decimal.Round((decimal)document.BaselineAnnualPremium.Value, 2),
        Offers = document.Offers.Select(o => new ComparisonOffer
        {
            OfferId = o.OfferId,
            CarrierId = o.CarrierId,
            CarrierName = o.CarrierName,
            Tagline = o.Tagline,
            BrandColor = o.BrandColor,
            FinancialStrengthRating = o.FinancialStrengthRating,
            CoverageTier = o.CoverageTier,
            AnnualPremium = decimal.Round((decimal)o.AnnualPremium, 2),
            MonthlyPremium = decimal.Round((decimal)o.MonthlyPremium, 2),
            Deductible = decimal.Round((decimal)o.Deductible, 2),
            LimitsSummary = o.LimitsSummary,
            BodilyInjuryPerPerson = o.BodilyInjuryPerPerson,
            BodilyInjuryPerAccident = o.BodilyInjuryPerAccident,
            PropertyDamage = o.PropertyDamage,
            IncludesRoadside = o.IncludesRoadside,
            IncludesRentalReimbursement = o.IncludesRentalReimbursement,
            DiscountAmount = decimal.Round((decimal)o.DiscountAmount, 2),
            AppliedDiscounts = o.AppliedDiscounts,
            RatingTrace = o.RatingTrace,
            IsBestPrice = o.IsBestPrice
        }).ToList(),
        Declinations = document.Declinations.Select(d => new CarrierDeclination
        {
            CarrierId = d.CarrierId,
            CarrierName = d.CarrierName,
            Reason = d.Reason,
            WasUnavailable = d.WasUnavailable
        }).ToList(),
        GeneratedUtc = document.GeneratedUtc,
        CorrelationId = document.CorrelationId,
        TotalRatingMs = document.TotalRatingMs
    };
}
