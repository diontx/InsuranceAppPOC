using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Carriers;

/// <summary>
/// The five mock carriers this POC shops a quote against, expressed as the
/// canonical seed for the NoSQL catalog.
/// </summary>
/// <remarks>
/// <para>
/// Every carrier here is <b>fictional</b>. Names, factors, discounts, limits and
/// financial-strength grades are invented so the demo produces deterministic,
/// explainable numbers. Nothing in this file models a real insurance company, a
/// real rate filing, or real actuarial methodology.
/// </para>
/// <para>
/// At runtime the catalog is read from MongoDB through EF Core so an operator can
/// change pricing without redeploying code. This class is the seed of record and
/// the fallback the rating engine uses when the catalog is unreachable.
/// </para>
/// </remarks>
public static class CarrierPanel
{
    /// <summary>Deductible/limit package shared by carriers that write standard forms.</summary>
    private static IReadOnlyDictionary<CoverageTier, CoverageDefinition> StandardCoverages(
        decimal basicDeductible,
        decimal standardDeductible,
        decimal premiumDeductible) =>
        new Dictionary<CoverageTier, CoverageDefinition>
        {
            [CoverageTier.Basic] = new()
            {
                Deductible = basicDeductible,
                BodilyInjuryPerPerson = 25_000,
                BodilyInjuryPerAccident = 50_000,
                PropertyDamage = 25_000,
                IncludesRoadside = false,
                IncludesRentalReimbursement = false
            },
            [CoverageTier.Standard] = new()
            {
                Deductible = standardDeductible,
                BodilyInjuryPerPerson = 50_000,
                BodilyInjuryPerAccident = 100_000,
                PropertyDamage = 50_000,
                IncludesRoadside = true,
                IncludesRentalReimbursement = false
            },
            [CoverageTier.Premium] = new()
            {
                Deductible = premiumDeductible,
                BodilyInjuryPerPerson = 250_000,
                BodilyInjuryPerAccident = 500_000,
                PropertyDamage = 100_000,
                IncludesRoadside = true,
                IncludesRentalReimbursement = true
            }
        };

    /// <summary>
    /// The carrier panel. Ordering is stable so seeded data and test expectations
    /// do not drift.
    /// </summary>
    public static IReadOnlyList<CarrierProfile> All { get; } =
    [
        // ------------------------------------------------------------------
        // 1. Liberty Crest — broad appetite, cheapest on clean risks.
        // ------------------------------------------------------------------
        new CarrierProfile
        {
            CarrierId = "liberty-crest",
            DisplayName = "Liberty Crest Insurance",
            Tagline = "Nationwide coverage with a clean-driver advantage.",
            BrandColor = "#1F3C88",
            FinancialStrengthRating = "A+",
            BaseAnnualPremium = 880m,
            NewVehicleFactor = 1.14m,
            HighMileageFactor = 1.18m,
            ThinPriorCoverageFactor = 1.32m,
            StateFactors = new Dictionary<string, decimal>
            {
                ["GA"] = 1.00m, ["FL"] = 1.22m, ["TX"] = 1.09m, ["NC"] = 0.96m,
                ["SC"] = 0.98m, ["TN"] = 0.94m, ["AL"] = 0.97m, ["VA"] = 0.99m
            },
            TierFactors = new Dictionary<CoverageTier, decimal>
            {
                [CoverageTier.Basic] = 0.82m,
                [CoverageTier.Standard] = 1.00m,
                [CoverageTier.Premium] = 1.34m
            },
            Coverages = StandardCoverages(1000m, 500m, 250m),
            Discounts =
            [
                new DiscountRule { Name = "Continuous coverage (24+ months)", Kind = DiscountKind.ContinuousCoverage, Percentage = 0.10m, Threshold = 24 },
                new DiscountRule { Name = "Low mileage (under 8,000)", Kind = DiscountKind.LowMileage, Percentage = 0.06m, Threshold = 8_000 },
                new DiscountRule { Name = "Paperless", Kind = DiscountKind.PaperlessDigital, Percentage = 0.03m }
            ],
            MonthlyInstalmentFee = 6.00m,
            MinimumPriorInsuranceMonths = 0,
            MaximumAnnualMileage = 30_000,
            SimulatedLatencyMsMin = 250,
            SimulatedLatencyMsMax = 450
        },

        // ------------------------------------------------------------------
        // 2. Summit Shield Mutual — rewards long continuous coverage.
        // ------------------------------------------------------------------
        new CarrierProfile
        {
            CarrierId = "summit-shield",
            DisplayName = "Summit Shield Mutual",
            Tagline = "Loyalty pricing that improves every renewal.",
            BrandColor = "#B3202C",
            FinancialStrengthRating = "A",
            BaseAnnualPremium = 935m,
            NewVehicleFactor = 1.08m,
            HighMileageFactor = 1.25m,
            ThinPriorCoverageFactor = 1.45m,
            StateFactors = new Dictionary<string, decimal>
            {
                ["GA"] = 0.97m, ["FL"] = 1.28m, ["TX"] = 1.12m, ["NC"] = 0.93m,
                ["SC"] = 0.95m, ["TN"] = 0.91m, ["VA"] = 0.96m
            },
            TierFactors = new Dictionary<CoverageTier, decimal>
            {
                [CoverageTier.Basic] = 0.78m,
                [CoverageTier.Standard] = 1.00m,
                [CoverageTier.Premium] = 1.28m
            },
            Coverages = StandardCoverages(1000m, 750m, 250m),
            Discounts =
            [
                new DiscountRule { Name = "Continuous coverage (36+ months)", Kind = DiscountKind.ContinuousCoverage, Percentage = 0.14m, Threshold = 36 },
                new DiscountRule { Name = "Full coverage package", Kind = DiscountKind.FullCoveragePackage, Percentage = 0.07m },
                new DiscountRule { Name = "Paperless", Kind = DiscountKind.PaperlessDigital, Percentage = 0.02m }
            ],
            MonthlyInstalmentFee = 5.00m,
            // Will not write a driver with no verifiable prior coverage.
            MinimumPriorInsuranceMonths = 6,
            MaximumAnnualMileage = 25_000,
            SimulatedLatencyMsMin = 300,
            SimulatedLatencyMsMax = 600
        },

        // ------------------------------------------------------------------
        // 3. BlueRidge Assurance — low-mileage and commuter specialist.
        // ------------------------------------------------------------------
        new CarrierProfile
        {
            CarrierId = "blueridge-assurance",
            DisplayName = "BlueRidge Assurance",
            Tagline = "Built for commuters who drive less than most.",
            BrandColor = "#2E6FA7",
            FinancialStrengthRating = "A-",
            BaseAnnualPremium = 820m,
            NewVehicleFactor = 1.20m,
            // Punishes high mileage hard, rewards low mileage generously.
            HighMileageFactor = 1.42m,
            ThinPriorCoverageFactor = 1.28m,
            StateFactors = new Dictionary<string, decimal>
            {
                ["GA"] = 1.02m, ["NC"] = 0.94m, ["SC"] = 0.97m, ["TN"] = 0.92m,
                ["VA"] = 0.98m, ["AL"] = 1.00m
                // Deliberately does not write FL or TX, so the panel varies by state.
            },
            TierFactors = new Dictionary<CoverageTier, decimal>
            {
                [CoverageTier.Basic] = 0.85m,
                [CoverageTier.Standard] = 1.00m,
                [CoverageTier.Premium] = 1.22m
            },
            Coverages = StandardCoverages(1500m, 500m, 500m),
            Discounts =
            [
                new DiscountRule { Name = "Low mileage (under 12,000)", Kind = DiscountKind.LowMileage, Percentage = 0.12m, Threshold = 12_000 },
                new DiscountRule { Name = "Continuous coverage (12+ months)", Kind = DiscountKind.ContinuousCoverage, Percentage = 0.06m, Threshold = 12 },
                new DiscountRule { Name = "Paperless", Kind = DiscountKind.PaperlessDigital, Percentage = 0.04m }
            ],
            MonthlyInstalmentFee = 4.50m,
            MinimumPriorInsuranceMonths = 0,
            MaximumAnnualMileage = 20_000,
            SimulatedLatencyMsMin = 200,
            SimulatedLatencyMsMax = 400
        },

        // ------------------------------------------------------------------
        // 4. Patriot Vale Casualty — non-standard / high-risk appetite.
        // ------------------------------------------------------------------
        new CarrierProfile
        {
            CarrierId = "patriot-vale",
            DisplayName = "Patriot Vale Casualty",
            Tagline = "Coverage for drivers other carriers turn away.",
            BrandColor = "#8C1D2C",
            FinancialStrengthRating = "B++",
            // Highest base, but the only carrier that writes thin prior coverage cheaply.
            BaseAnnualPremium = 1_050m,
            NewVehicleFactor = 1.10m,
            HighMileageFactor = 1.12m,
            // Barely surcharges a coverage lapse; that is its whole market.
            ThinPriorCoverageFactor = 1.06m,
            StateFactors = new Dictionary<string, decimal>
            {
                ["GA"] = 1.00m, ["FL"] = 1.15m, ["TX"] = 1.05m, ["NC"] = 1.00m,
                ["SC"] = 1.02m, ["TN"] = 0.98m, ["AL"] = 1.01m, ["VA"] = 1.03m, ["MS"] = 1.04m
            },
            TierFactors = new Dictionary<CoverageTier, decimal>
            {
                [CoverageTier.Basic] = 0.74m,
                [CoverageTier.Standard] = 1.00m,
                [CoverageTier.Premium] = 1.45m
            },
            Coverages = StandardCoverages(2000m, 1000m, 500m),
            Discounts =
            [
                new DiscountRule { Name = "Paperless", Kind = DiscountKind.PaperlessDigital, Percentage = 0.02m },
                new DiscountRule { Name = "Continuous coverage (6+ months)", Kind = DiscountKind.ContinuousCoverage, Percentage = 0.05m, Threshold = 6 }
            ],
            MonthlyInstalmentFee = 9.00m,
            MinimumPriorInsuranceMonths = 0,
            MaximumAnnualMileage = 40_000,
            SimulatedLatencyMsMin = 400,
            SimulatedLatencyMsMax = 750
        },

        // ------------------------------------------------------------------
        // 5. Ironclad Direct — direct-to-consumer, lowest expense load.
        // ------------------------------------------------------------------
        new CarrierProfile
        {
            CarrierId = "ironclad-direct",
            DisplayName = "Ironclad Direct",
            Tagline = "No agents, no overhead, no surprises.",
            BrandColor = "#14477D",
            FinancialStrengthRating = "A",
            BaseAnnualPremium = 795m,
            NewVehicleFactor = 1.22m,
            HighMileageFactor = 1.20m,
            // Strict on lapses: the cheap base is only for continuously insured drivers.
            ThinPriorCoverageFactor = 1.58m,
            StateFactors = new Dictionary<string, decimal>
            {
                ["GA"] = 1.01m, ["FL"] = 1.35m, ["TX"] = 1.08m, ["NC"] = 0.95m,
                ["SC"] = 0.99m, ["TN"] = 0.93m, ["VA"] = 0.97m
            },
            TierFactors = new Dictionary<CoverageTier, decimal>
            {
                [CoverageTier.Basic] = 0.80m,
                [CoverageTier.Standard] = 1.00m,
                [CoverageTier.Premium] = 1.30m
            },
            Coverages = StandardCoverages(1000m, 500m, 100m),
            Discounts =
            [
                new DiscountRule { Name = "Continuous coverage (18+ months)", Kind = DiscountKind.ContinuousCoverage, Percentage = 0.11m, Threshold = 18 },
                new DiscountRule { Name = "New vehicle safety (3 model years)", Kind = DiscountKind.NewVehicleSafety, Percentage = 0.08m, Threshold = 3 },
                new DiscountRule { Name = "Low mileage (under 10,000)", Kind = DiscountKind.LowMileage, Percentage = 0.07m, Threshold = 10_000 },
                new DiscountRule { Name = "Paperless", Kind = DiscountKind.PaperlessDigital, Percentage = 0.05m }
            ],
            MonthlyInstalmentFee = 3.00m,
            MinimumPriorInsuranceMonths = 12,
            MaximumAnnualMileage = 22_000,
            SimulatedLatencyMsMin = 150,
            SimulatedLatencyMsMax = 350
        }
    ];

    /// <summary>Looks up a carrier by its catalog key, or null when unknown.</summary>
    public static CarrierProfile? Find(string carrierId) =>
        All.FirstOrDefault(c => c.CarrierId.Equals(carrierId, StringComparison.OrdinalIgnoreCase));
}
