using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Infrastructure.NoSql.Documents;

namespace PolicyFlow.Infrastructure.NoSql.Persistence;

/// <summary>
/// Reads the carrier panel from MongoDB through EF Core.
/// </summary>
/// <remarks>
/// <para>
/// Falls back to <see cref="CarrierPanel.All"/> when Mongo is unreachable. That is
/// a considered trade-off: the carrier panel is <i>configuration</i>, and the
/// compiled-in seed is the same data the catalog is seeded from, so serving it is
/// correct rather than a guess. Failing the whole quote instead would take the
/// demo down over a cache outage.
/// </para>
/// <para>
/// The fallback is logged as an error, not silently. Silently serving stale
/// pricing is how a system ends up quoting last quarter's rates for a month before
/// anyone notices.
/// </para>
/// </remarks>
public sealed class MongoCarrierCatalog(
    CarrierCatalogDbContext db,
    ILogger<MongoCarrierCatalog> logger) : ICarrierCatalog
{
    public async Task<IReadOnlyList<CarrierProfile>> GetActiveCarriersAsync(CancellationToken ct = default)
    {
        try
        {
            var documents = await db.Carriers
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder)
                .ToListAsync(ct);

            if (documents.Count > 0)
            {
                return documents.Select(ToProfile).ToList();
            }

            logger.LogWarning(
                "Carrier catalog is empty; using the compiled-in panel. " +
                "Operation={Operation} Result={Result}",
                "GetActiveCarriers", "EmptyCatalogFallback");

            return CarrierPanel.All;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Carrier catalog is unreachable; using the compiled-in panel. Pricing configuration " +
                "changes made in MongoDB will NOT be reflected until it recovers. " +
                "Operation={Operation} Result={Result}",
                "GetActiveCarriers", "CatalogUnavailableFallback");

            return CarrierPanel.All;
        }
    }

    public async Task<CarrierProfile?> GetCarrierAsync(string carrierId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(carrierId))
        {
            return null;
        }

        try
        {
            var document = await db.Carriers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.CarrierId == carrierId && c.IsActive, ct);

            return document is not null ? ToProfile(document) : CarrierPanel.Find(carrierId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Carrier lookup failed; using the compiled-in panel. Operation={Operation} " +
                "CarrierId={CarrierId} Result={Result}",
                "GetCarrier", carrierId, "CatalogUnavailableFallback");

            return CarrierPanel.Find(carrierId);
        }
    }

    /// <summary>
    /// Seeds the catalog from <see cref="CarrierPanel.All"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent by carrier id: existing documents are updated in place rather
    /// than duplicated, so re-running it on every container start is safe and an
    /// operator's manual edits to an unchanged carrier are the only thing at risk
    /// (they are overwritten, which is the intended "reset to canonical" behaviour
    /// for a demo).
    /// </remarks>
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        var existing = await db.Carriers.ToListAsync(ct);
        var byId = existing.ToDictionary(c => c.CarrierId, StringComparer.OrdinalIgnoreCase);
        var written = 0;

        for (var i = 0; i < CarrierPanel.All.Count; i++)
        {
            var profile = CarrierPanel.All[i];

            if (byId.TryGetValue(profile.CarrierId, out var document))
            {
                Apply(profile, document, i);
                db.Carriers.Update(document);
            }
            else
            {
                var created = new CarrierDocument { CarrierId = profile.CarrierId };
                Apply(profile, created, i);
                await db.Carriers.AddAsync(created, ct);
            }

            written++;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Carrier catalog seeded. Operation={Operation} CarrierCount={CarrierCount} Result={Result}",
            "SeedCarrierCatalog", written, "Seeded");

        return written;
    }

    /// <summary>Copies a domain profile onto a document, preserving its identity.</summary>
    private static void Apply(CarrierProfile profile, CarrierDocument document, int sortOrder)
    {
        document.CarrierId = profile.CarrierId;
        document.DisplayName = profile.DisplayName;
        document.Tagline = profile.Tagline;
        document.BrandColor = profile.BrandColor;
        document.FinancialStrengthRating = profile.FinancialStrengthRating;
        document.BaseAnnualPremium = (double)profile.BaseAnnualPremium;
        document.NewVehicleFactor = (double)profile.NewVehicleFactor;
        document.HighMileageFactor = (double)profile.HighMileageFactor;
        document.ThinPriorCoverageFactor = (double)profile.ThinPriorCoverageFactor;
        document.StateFactors = profile.StateFactors.ToDictionary(kv => kv.Key, kv => (double)kv.Value);
        document.TierFactors = profile.TierFactors.ToDictionary(kv => kv.Key.ToString(), kv => (double)kv.Value);
        // A list, with the tier carried as a field. The EF Mongo provider cannot
        // map a dictionary whose value is a complex type.
        document.Coverages = profile.Coverages
            .OrderBy(kv => kv.Key)
            .Select(kv => new CoverageSubDocument
            {
                Tier = kv.Key.ToString(),
                Deductible = (double)kv.Value.Deductible,
                BodilyInjuryPerPerson = kv.Value.BodilyInjuryPerPerson,
                BodilyInjuryPerAccident = kv.Value.BodilyInjuryPerAccident,
                PropertyDamage = kv.Value.PropertyDamage,
                IncludesRoadside = kv.Value.IncludesRoadside,
                IncludesRentalReimbursement = kv.Value.IncludesRentalReimbursement
            })
            .ToList();
        document.Discounts = profile.Discounts
            .Select(d => new DiscountSubDocument
            {
                Name = d.Name,
                Kind = d.Kind.ToString(),
                Percentage = (double)d.Percentage,
                Threshold = d.Threshold
            })
            .ToList();
        document.MonthlyInstalmentFee = (double)profile.MonthlyInstalmentFee;
        document.MinimumPriorInsuranceMonths = profile.MinimumPriorInsuranceMonths;
        document.MaximumAnnualMileage = profile.MaximumAnnualMileage;
        document.SimulatedLatencyMsMin = profile.SimulatedLatencyMsMin;
        document.SimulatedLatencyMsMax = profile.SimulatedLatencyMsMax;
        document.IsActive = true;
        document.SortOrder = sortOrder;
        document.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>Maps a document back to the domain profile the rating engine uses.</summary>
    private static CarrierProfile ToProfile(CarrierDocument document) => new()
    {
        CarrierId = document.CarrierId,
        DisplayName = document.DisplayName,
        Tagline = document.Tagline,
        BrandColor = document.BrandColor,
        FinancialStrengthRating = document.FinancialStrengthRating,
        BaseAnnualPremium = (decimal)document.BaseAnnualPremium,
        NewVehicleFactor = (decimal)document.NewVehicleFactor,
        HighMileageFactor = (decimal)document.HighMileageFactor,
        ThinPriorCoverageFactor = (decimal)document.ThinPriorCoverageFactor,
        StateFactors = document.StateFactors.ToDictionary(
            kv => kv.Key.ToUpperInvariant(),
            kv => (decimal)kv.Value),
        TierFactors = document.TierFactors
            // Skip any tier name the document carries that this build does not
            // recognise, rather than throwing. Forward compatibility: a catalog
            // written by a newer service must not break an older one.
            .Where(kv => Enum.TryParse<CoverageTier>(kv.Key, ignoreCase: true, out _))
            .ToDictionary(
                kv => Enum.Parse<CoverageTier>(kv.Key, ignoreCase: true),
                kv => (decimal)kv.Value),

        Coverages = document.Coverages
            // Skip an unrecognised tier name rather than throwing. Forward
            // compatibility: a catalog written by a newer service must not break
            // an older one that does not know the new tier.
            .Where(c => Enum.TryParse<CoverageTier>(c.Tier, ignoreCase: true, out _))
            // DistinctBy guards against a hand-edited document carrying the same
            // tier twice, which would otherwise throw on ToDictionary.
            .DistinctBy(c => Enum.Parse<CoverageTier>(c.Tier, ignoreCase: true))
            .ToDictionary(
                c => Enum.Parse<CoverageTier>(c.Tier, ignoreCase: true),
                c => new CoverageDefinition
                {
                    Deductible = (decimal)c.Deductible,
                    BodilyInjuryPerPerson = c.BodilyInjuryPerPerson,
                    BodilyInjuryPerAccident = c.BodilyInjuryPerAccident,
                    PropertyDamage = c.PropertyDamage,
                    IncludesRoadside = c.IncludesRoadside,
                    IncludesRentalReimbursement = c.IncludesRentalReimbursement
                }),
        Discounts = document.Discounts
            .Where(d => Enum.TryParse<DiscountKind>(d.Kind, ignoreCase: true, out _))
            .Select(d => new DiscountRule
            {
                Name = d.Name,
                Kind = Enum.Parse<DiscountKind>(d.Kind, ignoreCase: true),
                Percentage = (decimal)d.Percentage,
                Threshold = d.Threshold
            })
            .ToList(),
        MonthlyInstalmentFee = (decimal)document.MonthlyInstalmentFee,
        MinimumPriorInsuranceMonths = document.MinimumPriorInsuranceMonths,
        MaximumAnnualMileage = document.MaximumAnnualMileage,
        SimulatedLatencyMsMin = document.SimulatedLatencyMsMin,
        SimulatedLatencyMsMax = document.SimulatedLatencyMsMax
    };
}
