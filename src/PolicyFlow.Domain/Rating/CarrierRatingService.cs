using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Rating;

/// <summary>
/// Prices one quote against one carrier, producing one offer per coverage tier the
/// carrier will write.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pricing model is fictional.</b> It exists to produce deterministic,
/// hand-verifiable numbers that differ plausibly between carriers, not to model
/// real rate filings.
/// </para>
/// <para>The multiplicative model, applied per tier:</para>
/// <code>
/// premium = carrier.BaseAnnualPremium
///         * stateFactor
///         * (newVehicle       ? NewVehicleFactor        : 1)
///         * (highMileage      ? HighMileageFactor       : 1)
///         * (thinPriorMonths  ? ThinPriorCoverageFactor : 1)
///         * tierFactor
///         * (1 - totalDiscountPercentage)
/// </code>
/// <para>
/// Multiplicative factors are used rather than the flat surcharges in
/// <see cref="QuoteRatingService"/> deliberately: the two models disagree, which
/// is exactly the point of a comparison screen. The house baseline is a reference
/// figure; the carrier panel is what the customer can actually buy.
/// </para>
/// <para>
/// Carriers decline a risk outright via appetite rules
/// (<see cref="CarrierProfile.MinimumPriorInsuranceMonths"/>,
/// <see cref="CarrierProfile.MaximumAnnualMileage"/>, state availability), which is
/// why a rated quote may carry fewer than fifteen offers.
/// </para>
/// </remarks>
public sealed class CarrierRatingService
{
    /// <summary>Total discount percentage is capped so premiums stay sane.</summary>
    public const decimal MaximumTotalDiscount = 0.30m;

    /// <summary>
    /// Produces every offer this carrier will make for the quote. Returns an empty
    /// list when the carrier has no appetite for the risk.
    /// </summary>
    public IReadOnlyList<CarrierOffer> RateCarrier(Quote quote, CarrierProfile carrier, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(carrier);

        if (!IsInAppetite(quote, carrier, out _))
        {
            return [];
        }

        var stateFactor = carrier.StateFactors[quote.State];
        var isNewVehicle = quote.VehicleYear >= nowUtc.Year - QuoteRatingService.NewVehicleWindowYears;
        var isHighMileage = quote.AnnualMileage > QuoteRatingService.HighMileageThreshold;
        var hasThinPriorCoverage = quote.PriorInsuranceMonths < QuoteRatingService.ThinPriorCoverageMonths;

        var offers = new List<CarrierOffer>(carrier.TierFactors.Count);

        foreach (var (tier, tierFactor) in carrier.TierFactors.OrderBy(t => t.Key))
        {
            if (!carrier.Coverages.TryGetValue(tier, out var coverage))
            {
                // Carrier does not file this tier; skip rather than guess limits.
                continue;
            }

            var trace = new List<string>
            {
                $"{carrier.DisplayName} — {tier} tier",
                string.Create(RatingFormat.Culture, $"Base annual premium: {carrier.BaseAnnualPremium:C}.")
            };

            var premium = carrier.BaseAnnualPremium;

            premium *= stateFactor;
            trace.Add(string.Create(RatingFormat.Culture,
                $"State factor {quote.State} x{stateFactor:0.00} -> {premium:C}."));

            if (isNewVehicle)
            {
                premium *= carrier.NewVehicleFactor;
                trace.Add(string.Create(RatingFormat.Culture,
                    $"New vehicle ({quote.VehicleYear}) x{carrier.NewVehicleFactor:0.00} -> {premium:C}."));
            }

            if (isHighMileage)
            {
                premium *= carrier.HighMileageFactor;
                trace.Add(string.Create(RatingFormat.Culture,
                    $"High mileage ({quote.AnnualMileage:N0}) x{carrier.HighMileageFactor:0.00} -> {premium:C}."));
            }

            if (hasThinPriorCoverage)
            {
                premium *= carrier.ThinPriorCoverageFactor;
                trace.Add(string.Create(RatingFormat.Culture,
                    $"Prior coverage under {QuoteRatingService.ThinPriorCoverageMonths} months " +
                    $"x{carrier.ThinPriorCoverageFactor:0.00} -> {premium:C}."));
            }

            premium *= tierFactor;
            trace.Add(string.Create(RatingFormat.Culture,
                $"{tier} tier factor x{tierFactor:0.00} -> {premium:C}."));

            var premiumBeforeDiscounts = premium;
            var (discountPercentage, discountNames) = EvaluateDiscounts(quote, carrier, tier, nowUtc);

            if (discountPercentage > 0m)
            {
                premium *= 1m - discountPercentage;
                trace.Add(string.Create(RatingFormat.Culture,
                    $"Discounts {discountPercentage:P0} ({string.Join(", ", discountNames)}) -> {premium:C}."));
            }
            else
            {
                trace.Add("No discounts applied.");
            }

            var annual = decimal.Round(premium, 2, MidpointRounding.AwayFromZero);
            var discountAmount = decimal.Round(premiumBeforeDiscounts - premium, 2, MidpointRounding.AwayFromZero);
            var monthly = decimal.Round((annual / 12m) + carrier.MonthlyInstalmentFee, 2, MidpointRounding.AwayFromZero);

            trace.Add(string.Create(RatingFormat.Culture,
                $"Final annual premium: {annual:C}; monthly {monthly:C} " +
                $"(includes {carrier.MonthlyInstalmentFee:C} instalment fee)."));

            offers.Add(CarrierOffer.Create(
                carrierId: carrier.CarrierId,
                carrierName: carrier.DisplayName,
                coverageTier: tier,
                annualPremium: annual,
                monthlyPremium: monthly,
                deductible: coverage.Deductible,
                bodilyInjuryPerPerson: coverage.BodilyInjuryPerPerson,
                bodilyInjuryPerAccident: coverage.BodilyInjuryPerAccident,
                propertyDamage: coverage.PropertyDamage,
                includesRoadside: coverage.IncludesRoadside,
                includesRentalReimbursement: coverage.IncludesRentalReimbursement,
                financialStrengthRating: carrier.FinancialStrengthRating,
                discountAmount: discountAmount,
                appliedDiscounts: discountNames,
                ratingTrace: trace,
                nowUtc: nowUtc));
        }

        return offers;
    }

    /// <summary>
    /// Tests the carrier's underwriting appetite. <paramref name="declineReason"/>
    /// is populated when the carrier will not write the risk, so the worker can log
    /// why the panel came back short.
    /// </summary>
    public bool IsInAppetite(Quote quote, CarrierProfile carrier, out string? declineReason)
    {
        if (!carrier.WritesInState(quote.State))
        {
            declineReason = $"{carrier.DisplayName} is not licensed in {quote.State}.";
            return false;
        }

        if (quote.PriorInsuranceMonths < carrier.MinimumPriorInsuranceMonths)
        {
            declineReason =
                $"{carrier.DisplayName} requires {carrier.MinimumPriorInsuranceMonths} months of prior coverage; " +
                $"quote has {quote.PriorInsuranceMonths}.";
            return false;
        }

        if (quote.AnnualMileage > carrier.MaximumAnnualMileage)
        {
            declineReason = string.Create(RatingFormat.Culture,
                $"{carrier.DisplayName} writes up to {carrier.MaximumAnnualMileage:N0} annual miles; " +
                $"quote has {quote.AnnualMileage:N0}.");
            return false;
        }

        declineReason = null;
        return true;
    }

    /// <summary>
    /// Sums the carrier's applicable discounts, capped at
    /// <see cref="MaximumTotalDiscount"/>. Percentages are additive before the cap,
    /// which keeps the arithmetic explainable on screen.
    /// </summary>
    private static (decimal Percentage, List<string> Names) EvaluateDiscounts(
        Quote quote,
        CarrierProfile carrier,
        CoverageTier tier,
        DateTime nowUtc)
    {
        var total = 0m;
        var names = new List<string>();

        foreach (var rule in carrier.Discounts)
        {
            var applies = rule.Kind switch
            {
                DiscountKind.ContinuousCoverage => quote.PriorInsuranceMonths >= rule.Threshold,
                DiscountKind.LowMileage => quote.AnnualMileage <= rule.Threshold,
                DiscountKind.NewVehicleSafety => quote.VehicleYear >= nowUtc.Year - rule.Threshold,
                DiscountKind.FullCoveragePackage => tier == CoverageTier.Premium,
                // Every channel in this POC is digital, including the legacy adapter,
                // which posts to the same API rather than mailing paper.
                DiscountKind.PaperlessDigital => true,
                _ => false
            };

            if (applies)
            {
                total += rule.Percentage;
                names.Add(rule.Name);
            }
        }

        if (total <= MaximumTotalDiscount)
        {
            return (total, names);
        }

        names.Add(string.Create(RatingFormat.Culture, $"capped at {MaximumTotalDiscount:P0}"));
        return (MaximumTotalDiscount, names);
    }
}
