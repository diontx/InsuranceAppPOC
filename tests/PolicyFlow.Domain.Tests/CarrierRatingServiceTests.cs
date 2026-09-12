using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Domain.Rating;

namespace PolicyFlow.Domain.Tests;

/// <summary>
/// Covers the five-carrier marketplace: appetite rules, per-carrier factors,
/// discounts and the shape of the resulting offer set.
/// </summary>
public class CarrierRatingServiceTests
{
    private readonly CarrierRatingService _sut = new();

    [Fact]
    public void PanelContainsExactlyFiveCarriers()
    {
        Assert.Equal(5, CarrierPanel.All.Count);
    }

    [Fact]
    public void CarrierIdsAreUnique()
    {
        var ids = CarrierPanel.All.Select(c => c.CarrierId).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryCarrierFilesAllThreeCoverageTiers()
    {
        foreach (var carrier in CarrierPanel.All)
        {
            Assert.Equal(3, carrier.TierFactors.Count);
            Assert.Equal(3, carrier.Coverages.Count);

            foreach (var tier in Enum.GetValues<CoverageTier>())
            {
                Assert.True(carrier.TierFactors.ContainsKey(tier), $"{carrier.CarrierId} missing tier factor {tier}");
                Assert.True(carrier.Coverages.ContainsKey(tier), $"{carrier.CarrierId} missing coverage {tier}");
            }
        }
    }

    [Fact]
    public void EveryCarrierWritesInTheHomeState()
    {
        // GA is the program's home state, so a GA quote must always produce a full panel.
        foreach (var carrier in CarrierPanel.All)
        {
            Assert.True(carrier.WritesInState("GA"), $"{carrier.CarrierId} does not write GA");
        }
    }

    [Fact]
    public void CleanGeorgiaRisk_ProducesThreeOffersFromEveryCarrier()
    {
        var quote = TestQuotes.Clean(state: "GA", priorInsuranceMonths: 36, annualMileage: 10_000);

        var allOffers = CarrierPanel.All
            .SelectMany(c => _sut.RateCarrier(quote, c, TestQuotes.Now))
            .ToList();

        // 5 carriers x 3 tiers
        Assert.Equal(15, allOffers.Count);
        Assert.Equal(5, allOffers.Select(o => o.CarrierId).Distinct().Count());
    }

    [Fact]
    public void PremiumIncreasesFromBasicToStandardToPremium()
    {
        var quote = TestQuotes.Clean();

        foreach (var carrier in CarrierPanel.All)
        {
            var offers = _sut.RateCarrier(quote, carrier, TestQuotes.Now)
                .ToDictionary(o => o.CoverageTier);

            Assert.True(
                offers[CoverageTier.Basic].AnnualPremium < offers[CoverageTier.Standard].AnnualPremium,
                $"{carrier.CarrierId}: Basic should cost less than Standard");
            Assert.True(
                offers[CoverageTier.Standard].AnnualPremium < offers[CoverageTier.Premium].AnnualPremium,
                $"{carrier.CarrierId}: Standard should cost less than Premium");
        }
    }

    [Fact]
    public void HigherTiersCarryHigherLimitsAndLowerDeductibles()
    {
        var quote = TestQuotes.Clean();

        foreach (var carrier in CarrierPanel.All)
        {
            var offers = _sut.RateCarrier(quote, carrier, TestQuotes.Now)
                .ToDictionary(o => o.CoverageTier);

            Assert.True(offers[CoverageTier.Premium].BodilyInjuryPerPerson > offers[CoverageTier.Basic].BodilyInjuryPerPerson);
            Assert.True(offers[CoverageTier.Premium].Deductible < offers[CoverageTier.Basic].Deductible);
            Assert.True(offers[CoverageTier.Premium].IncludesRentalReimbursement);
            Assert.False(offers[CoverageTier.Basic].IncludesRoadside);
        }
    }

    [Fact]
    public void CarrierNotLicensedInState_MakesNoOffer()
    {
        // BlueRidge deliberately does not write Florida.
        var blueRidge = CarrierPanel.Find("blueridge-assurance")!;
        var floridaQuote = TestQuotes.Clean(state: "FL");

        var offers = _sut.RateCarrier(floridaQuote, blueRidge, TestQuotes.Now);

        Assert.Empty(offers);
        Assert.False(_sut.IsInAppetite(floridaQuote, blueRidge, out var reason));
        Assert.Contains("not licensed", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CarrierRequiringPriorCoverage_DeclinesAThinFile()
    {
        // Ironclad requires 12 months of continuous coverage.
        var ironclad = CarrierPanel.Find("ironclad-direct")!;
        var thinFile = TestQuotes.Clean(priorInsuranceMonths: 3);

        var offers = _sut.RateCarrier(thinFile, ironclad, TestQuotes.Now);

        Assert.Empty(offers);
        Assert.False(_sut.IsInAppetite(thinFile, ironclad, out var reason));
        Assert.Contains("prior coverage", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CarrierWithMileageCap_DeclinesAHighMileageRisk()
    {
        // BlueRidge writes up to 20,000 annual miles.
        var blueRidge = CarrierPanel.Find("blueridge-assurance")!;
        var highMileage = TestQuotes.Clean(annualMileage: 25_000);

        Assert.Empty(_sut.RateCarrier(highMileage, blueRidge, TestQuotes.Now));
        Assert.False(_sut.IsInAppetite(highMileage, blueRidge, out var reason));
        Assert.Contains("annual miles", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThinPriorCoverage_ShrinksThePanelButPatriotValeStillWrites()
    {
        // Patriot Vale is the non-standard market: it is the whole reason a
        // lapsed-coverage customer still sees an offer.
        var thinFile = TestQuotes.Clean(priorInsuranceMonths: 2);

        var writingCarriers = CarrierPanel.All
            .Where(c => _sut.RateCarrier(thinFile, c, TestQuotes.Now).Count > 0)
            .Select(c => c.CarrierId)
            .ToList();

        Assert.Contains("patriot-vale", writingCarriers);
        Assert.DoesNotContain("summit-shield", writingCarriers);   // needs 6 months
        Assert.DoesNotContain("ironclad-direct", writingCarriers); // needs 12 months
    }

    [Fact]
    public void ContinuousCoverageDiscount_ReducesPremium()
    {
        var libertyCrest = CarrierPanel.Find("liberty-crest")!;

        // 24 months hits the continuous-coverage discount; 12 months does not.
        var withDiscount = _sut
            .RateCarrier(TestQuotes.Clean(priorInsuranceMonths: 24), libertyCrest, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Standard);
        var withoutDiscount = _sut
            .RateCarrier(TestQuotes.Clean(priorInsuranceMonths: 12), libertyCrest, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Standard);

        Assert.True(withDiscount.AnnualPremium < withoutDiscount.AnnualPremium);
        Assert.Contains("Continuous coverage", withDiscount.AppliedDiscounts);
        Assert.True(withDiscount.DiscountAmount > 0m);
    }

    [Fact]
    public void LowMileageDiscount_AppliesAtOrBelowThreshold()
    {
        // BlueRidge gives 12% under 12,000 miles.
        var blueRidge = CarrierPanel.Find("blueridge-assurance")!;

        var lowMileage = _sut
            .RateCarrier(TestQuotes.Clean(annualMileage: 12_000), blueRidge, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Standard);
        var higherMileage = _sut
            .RateCarrier(TestQuotes.Clean(annualMileage: 12_001), blueRidge, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Standard);

        Assert.Contains("Low mileage", lowMileage.AppliedDiscounts);
        Assert.True(lowMileage.AnnualPremium < higherMileage.AnnualPremium);
    }

    [Fact]
    public void FullCoveragePackageDiscount_AppliesOnlyToThePremiumTier()
    {
        var summitShield = CarrierPanel.Find("summit-shield")!;

        var offers = _sut.RateCarrier(TestQuotes.Clean(), summitShield, TestQuotes.Now)
            .ToDictionary(o => o.CoverageTier);

        Assert.Contains("Full coverage package", offers[CoverageTier.Premium].AppliedDiscounts);
        Assert.DoesNotContain("Full coverage package", offers[CoverageTier.Basic].AppliedDiscounts);
        Assert.DoesNotContain("Full coverage package", offers[CoverageTier.Standard].AppliedDiscounts);
    }

    [Fact]
    public void TotalDiscountIsCapped()
    {
        // Ironclad can stack continuous coverage (11%) + new vehicle (8%)
        // + low mileage (7%) + paperless (5%) = 31%, above the 30% cap.
        var ironclad = CarrierPanel.Find("ironclad-direct")!;
        var maximallyDiscounted = TestQuotes.Clean(
            vehicleYear: TestQuotes.CurrentYear,
            annualMileage: 5_000,
            priorInsuranceMonths: 48);

        var offer = _sut
            .RateCarrier(maximallyDiscounted, ironclad, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Standard);

        Assert.Contains("capped", offer.AppliedDiscounts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StateFactor_ChangesPremiumBetweenStates()
    {
        var libertyCrest = CarrierPanel.Find("liberty-crest")!;

        // Liberty Crest: GA 1.00, FL 1.22.
        var georgia = _sut
            .RateCarrier(TestQuotes.Clean(state: "GA"), libertyCrest, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Standard);
        var florida = _sut
            .RateCarrier(TestQuotes.Clean(state: "FL"), libertyCrest, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Standard);

        Assert.True(florida.AnnualPremium > georgia.AnnualPremium);
    }

    [Fact]
    public void NewVehicleFactor_IncreasesPremium()
    {
        var summitShield = CarrierPanel.Find("summit-shield")!;

        var newVehicle = _sut
            .RateCarrier(TestQuotes.Clean(vehicleYear: TestQuotes.CurrentYear), summitShield, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Basic);
        var olderVehicle = _sut
            .RateCarrier(TestQuotes.Clean(vehicleYear: TestQuotes.CurrentYear - 5), summitShield, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Basic);

        Assert.True(newVehicle.AnnualPremium > olderVehicle.AnnualPremium);
    }

    [Fact]
    public void RatingIsDeterministic_SameInputProducesSamePremium()
    {
        // This is the property that makes the demo trustworthy: no randomness.
        var carrier = CarrierPanel.Find("liberty-crest")!;

        var first = _sut.RateCarrier(TestQuotes.Clean(), carrier, TestQuotes.Now);
        var second = _sut.RateCarrier(TestQuotes.Clean(), carrier, TestQuotes.Now);

        Assert.Equal(
            first.Select(o => (o.CoverageTier, o.AnnualPremium)),
            second.Select(o => (o.CoverageTier, o.AnnualPremium)));
    }

    [Fact]
    public void MonthlyPremiumIncludesTheCarrierInstalmentFee()
    {
        var carrier = CarrierPanel.Find("patriot-vale")!;

        var offer = _sut
            .RateCarrier(TestQuotes.Clean(), carrier, TestQuotes.Now)
            .Single(o => o.CoverageTier == CoverageTier.Standard);

        var expected = decimal.Round((offer.AnnualPremium / 12m) + carrier.MonthlyInstalmentFee, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, offer.MonthlyPremium);
    }

    [Fact]
    public void EveryOfferCarriesAnExplainableRatingTrace()
    {
        var offers = _sut.RateCarrier(TestQuotes.Clean(state: "FL"), CarrierPanel.Find("liberty-crest")!, TestQuotes.Now);

        foreach (var offer in offers)
        {
            Assert.Contains("Base annual premium", offer.RatingTrace);
            Assert.Contains("State factor", offer.RatingTrace);
            Assert.Contains("Final annual premium", offer.RatingTrace);
        }
    }

    [Fact]
    public void PremiumsAreRoundedToCents()
    {
        var offers = CarrierPanel.All
            .SelectMany(c => _sut.RateCarrier(TestQuotes.Clean(state: "TX"), c, TestQuotes.Now));

        foreach (var offer in offers)
        {
            Assert.Equal(offer.AnnualPremium, decimal.Round(offer.AnnualPremium, 2));
            Assert.Equal(offer.MonthlyPremium, decimal.Round(offer.MonthlyPremium, 2));
        }
    }

    [Fact]
    public void CarriersProduceDifferentPrices_SoComparisonIsMeaningful()
    {
        var quote = TestQuotes.Clean();

        var standardPremiums = CarrierPanel.All
            .SelectMany(c => _sut.RateCarrier(quote, c, TestQuotes.Now))
            .Where(o => o.CoverageTier == CoverageTier.Standard)
            .Select(o => o.AnnualPremium)
            .ToList();

        Assert.Equal(5, standardPremiums.Count);
        Assert.Equal(5, standardPremiums.Distinct().Count());
    }
}
