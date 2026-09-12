using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Rating;

/// <summary>
/// Eligibility and baseline pricing for a submitted quote.
/// </summary>
/// <remarks>
/// <para>
/// <b>This algorithm is fictional.</b> It is a small set of deterministic,
/// documented rules taken from POC-INSTRUCTIONS.md section 9, chosen so that
/// tests have exact expected values and a reviewer can verify pricing by hand. It
/// is not an actuarial rating engine and does not represent how automobile
/// insurance is actually priced.
/// </para>
/// <para>
/// The rules, applied in order to a base premium of $900:
/// </para>
/// <list type="table">
///   <item><term>Prior insurance = 0 months</term><description>Referral (no premium)</description></item>
///   <item><term>Vehicle year &gt;= current year - 2</term><description>+$175</description></item>
///   <item><term>Annual mileage &gt; 15,000</term><description>+$200</description></item>
///   <item><term>Prior insurance &lt; 6 months</term><description>+$300</description></item>
///   <item><term>State is not GA</term><description>+$100</description></item>
/// </list>
/// <para>
/// The service is stateless and takes the current time as an argument so the
/// model-year rule is deterministic under test.
/// </para>
/// </remarks>
public sealed class QuoteRatingService
{
    /// <summary>Starting annual premium before any adjustment.</summary>
    public const decimal BaseAnnualPremium = 900m;

    /// <summary>Surcharge for a vehicle two model years old or newer.</summary>
    public const decimal NewVehicleSurcharge = 175m;

    /// <summary>Surcharge for annual mileage above <see cref="HighMileageThreshold"/>.</summary>
    public const decimal HighMileageSurcharge = 200m;

    /// <summary>Surcharge for fewer than <see cref="ThinPriorCoverageMonths"/> months of prior coverage.</summary>
    public const decimal ThinPriorCoverageSurcharge = 300m;

    /// <summary>Surcharge for risks garaged outside the home state.</summary>
    public const decimal OutOfHomeStateSurcharge = 100m;

    /// <summary>Mileage above which the high-mileage surcharge applies.</summary>
    public const int HighMileageThreshold = 15_000;

    /// <summary>Prior-coverage months below which the thin-coverage surcharge applies.</summary>
    public const int ThinPriorCoverageMonths = 6;

    /// <summary>Model-year window, in years, that counts as a new vehicle.</summary>
    public const int NewVehicleWindowYears = 2;

    /// <summary>The program's home state. Everything else carries a surcharge.</summary>
    public const string HomeState = "GA";

    /// <summary>
    /// Vehicles older than this many years are declined. This rule is an addition
    /// to the instruction document, included so the Declined status is reachable
    /// and testable rather than dead code.
    /// </summary>
    public const int MaximumVehicleAgeYears = 25;

    /// <summary>
    /// Evaluates eligibility and, when eligible, the baseline premium.
    /// Rules are evaluated in a fixed order and every step is recorded in
    /// <see cref="UnderwritingDecision.Trace"/>.
    /// </summary>
    public UnderwritingDecision Evaluate(Quote quote, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var trace = new List<string>();
        var currentYear = nowUtc.Year;

        // --- Eligibility gates run first: no point pricing a risk we will not write.
        var vehicleAge = currentYear - quote.VehicleYear;
        if (vehicleAge > MaximumVehicleAgeYears)
        {
            trace.Add($"DECLINE: vehicle age {vehicleAge}y exceeds the {MaximumVehicleAgeYears}y program maximum.");
            return UnderwritingDecision.Decline(
                $"Vehicles older than {MaximumVehicleAgeYears} model years are not eligible for this program.",
                trace);
        }

        if (quote.PriorInsuranceMonths == 0)
        {
            trace.Add("REFER: no verifiable prior insurance; manual underwriting required.");
            return UnderwritingDecision.Refer(
                "No prior insurance on record. A licensed underwriter must review this application.",
                trace);
        }

        // --- Pricing.
        var premium = BaseAnnualPremium;
        trace.Add(string.Create(RatingFormat.Culture, $"Base annual premium: {BaseAnnualPremium:C}."));

        if (quote.VehicleYear >= currentYear - NewVehicleWindowYears)
        {
            premium += NewVehicleSurcharge;
            trace.Add(string.Create(RatingFormat.Culture,
                $"Vehicle year {quote.VehicleYear} is within {NewVehicleWindowYears} model years: +{NewVehicleSurcharge:C}."));
        }

        if (quote.AnnualMileage > HighMileageThreshold)
        {
            premium += HighMileageSurcharge;
            trace.Add(string.Create(RatingFormat.Culture,
                $"Annual mileage {quote.AnnualMileage:N0} exceeds {HighMileageThreshold:N0}: +{HighMileageSurcharge:C}."));
        }

        if (quote.PriorInsuranceMonths < ThinPriorCoverageMonths)
        {
            premium += ThinPriorCoverageSurcharge;
            trace.Add(string.Create(RatingFormat.Culture,
                $"Prior insurance {quote.PriorInsuranceMonths} months is under {ThinPriorCoverageMonths}: +{ThinPriorCoverageSurcharge:C}."));
        }

        if (!string.Equals(quote.State, HomeState, StringComparison.OrdinalIgnoreCase))
        {
            premium += OutOfHomeStateSurcharge;
            trace.Add(string.Create(RatingFormat.Culture,
                $"Risk state {quote.State} is outside {HomeState}: +{OutOfHomeStateSurcharge:C}."));
        }

        trace.Add(string.Create(RatingFormat.Culture, $"Baseline annual premium: {premium:C}."));
        return UnderwritingDecision.Eligible(premium, trace);
    }
}
