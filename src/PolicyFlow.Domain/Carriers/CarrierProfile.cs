using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Carriers;

/// <summary>
/// Rating configuration for one mock carrier. This is the shape stored as a
/// document in the NoSQL catalog and handed to the rating engine, which is why it
/// is a plain data record with no behaviour: the catalog owns the numbers, the
/// rating service owns the arithmetic.
/// </summary>
/// <remarks>
/// All five carriers are fictional. The factors are invented for demonstration
/// and bear no relationship to real actuarial practice or real company filings.
/// </remarks>
public sealed record CarrierProfile
{
    /// <summary>Stable catalog key used in URLs and offer rows, e.g. <c>liberty-crest</c>.</summary>
    public required string CarrierId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Short marketing line shown on the comparison card.</summary>
    public required string Tagline { get; init; }

    /// <summary>Hex brand accent used by the React comparison cards.</summary>
    public required string BrandColor { get; init; }

    /// <summary>A.M.-Best-style grade, e.g. <c>A+</c>.</summary>
    public required string FinancialStrengthRating { get; init; }

    /// <summary>Carrier-specific starting premium before any factors.</summary>
    public required decimal BaseAnnualPremium { get; init; }

    /// <summary>Multiplier applied when the vehicle is two model years old or newer.</summary>
    public required decimal NewVehicleFactor { get; init; }

    /// <summary>Multiplier applied when annual mileage exceeds 15,000.</summary>
    public required decimal HighMileageFactor { get; init; }

    /// <summary>Multiplier applied when prior coverage is under six months.</summary>
    public required decimal ThinPriorCoverageFactor { get; init; }

    /// <summary>Per-state multipliers. States absent from this map are not written.</summary>
    public required IReadOnlyDictionary<string, decimal> StateFactors { get; init; }

    /// <summary>Premium multiplier per coverage tier.</summary>
    public required IReadOnlyDictionary<CoverageTier, decimal> TierFactors { get; init; }

    /// <summary>Coverage limits and deductible per tier.</summary>
    public required IReadOnlyDictionary<CoverageTier, CoverageDefinition> Coverages { get; init; }

    /// <summary>Discounts this carrier offers, evaluated against the quote.</summary>
    public required IReadOnlyList<DiscountRule> Discounts { get; init; }

    /// <summary>Flat dollars added to each monthly instalment.</summary>
    public required decimal MonthlyInstalmentFee { get; init; }

    /// <summary>
    /// Minimum months of prior coverage this carrier will write. A quote below the
    /// threshold gets no offer from this carrier, which is how the demo shows a
    /// partial panel instead of a uniform five-offer response.
    /// </summary>
    public required int MinimumPriorInsuranceMonths { get; init; }

    /// <summary>Maximum annual mileage this carrier will write.</summary>
    public required int MaximumAnnualMileage { get; init; }

    /// <summary>
    /// Simulated upstream latency band in milliseconds. Drives the awaited delay
    /// in the carrier gateway so the fan-out has realistic, staggered timing.
    /// </summary>
    public required int SimulatedLatencyMsMin { get; init; }

    public required int SimulatedLatencyMsMax { get; init; }

    /// <summary>True when the carrier writes business in the supplied state.</summary>
    public bool WritesInState(string state) => StateFactors.ContainsKey(state);
}

/// <summary>Limits and features attached to one coverage tier.</summary>
public sealed record CoverageDefinition
{
    public required decimal Deductible { get; init; }

    public required int BodilyInjuryPerPerson { get; init; }

    public required int BodilyInjuryPerAccident { get; init; }

    public required int PropertyDamage { get; init; }

    public required bool IncludesRoadside { get; init; }

    public required bool IncludesRentalReimbursement { get; init; }

    /// <summary>Customer-facing summary, e.g. <c>50/100/50 with $500 deductible</c>.</summary>
    public string LimitsSummary =>
        $"{BodilyInjuryPerPerson / 1000}/{BodilyInjuryPerAccident / 1000}/{PropertyDamage / 1000}";
}

/// <summary>
/// A conditional premium reduction. <see cref="Kind"/> selects the predicate the
/// rating service evaluates, keeping the catalog declarative and serialisable.
/// </summary>
public sealed record DiscountRule
{
    public required string Name { get; init; }

    public required DiscountKind Kind { get; init; }

    /// <summary>Fraction of premium removed, e.g. 0.08 for eight percent.</summary>
    public required decimal Percentage { get; init; }

    /// <summary>Numeric threshold the predicate compares against, where relevant.</summary>
    public int Threshold { get; init; }
}

public enum DiscountKind
{
    /// <summary>Prior coverage months at or above <see cref="DiscountRule.Threshold"/>.</summary>
    ContinuousCoverage = 0,

    /// <summary>Annual mileage at or below <see cref="DiscountRule.Threshold"/>.</summary>
    LowMileage = 1,

    /// <summary>Vehicle model year at or above current year minus threshold.</summary>
    NewVehicleSafety = 2,

    /// <summary>Applied only to the <see cref="CoverageTier.Premium"/> tier.</summary>
    FullCoveragePackage = 3,

    /// <summary>Applied to every quote arriving through a digital channel.</summary>
    PaperlessDigital = 4
}
