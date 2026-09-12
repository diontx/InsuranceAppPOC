using PolicyFlow.Domain.Rating;

namespace PolicyFlow.Domain.Tests;

/// <summary>
/// Covers every rule in <see cref="QuoteRatingService"/> individually and in
/// combination. Expected premiums are written as literal arithmetic so a reviewer
/// can check them without running the code.
/// </summary>
public class QuoteRatingServiceTests
{
    private readonly QuoteRatingService _sut = new();

    [Fact]
    public void CleanRisk_ChargesBasePremiumOnly()
    {
        var decision = _sut.Evaluate(TestQuotes.Clean(), TestQuotes.Now);

        Assert.Equal(UnderwritingOutcome.Eligible, decision.Outcome);
        Assert.Equal(900m, decision.BaselineAnnualPremium);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void NewVehicle_AddsNewVehicleSurcharge()
    {
        var quote = TestQuotes.Clean(vehicleYear: TestQuotes.CurrentYear);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        // 900 base + 175 new vehicle
        Assert.Equal(1_075m, decision.BaselineAnnualPremium);
    }

    [Theory]
    [InlineData(0)]  // current year
    [InlineData(1)]  // one year old
    [InlineData(2)]  // two years old: still inside the window
    public void VehicleWithinTwoModelYears_AddsSurcharge(int yearsOld)
    {
        var quote = TestQuotes.Clean(vehicleYear: TestQuotes.CurrentYear - yearsOld);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(1_075m, decision.BaselineAnnualPremium);
    }

    [Fact]
    public void VehicleThreeModelYearsOld_IsOutsideTheWindow()
    {
        var quote = TestQuotes.Clean(vehicleYear: TestQuotes.CurrentYear - 3);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(900m, decision.BaselineAnnualPremium);
    }

    [Fact]
    public void HighMileage_AddsHighMileageSurcharge()
    {
        var quote = TestQuotes.Clean(annualMileage: 15_001);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        // 900 base + 200 high mileage
        Assert.Equal(1_100m, decision.BaselineAnnualPremium);
    }

    [Fact]
    public void MileageExactlyAtThreshold_DoesNotSurcharge()
    {
        // The rule is "> 15,000", so 15,000 itself must not be surcharged.
        var quote = TestQuotes.Clean(annualMileage: 15_000);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(900m, decision.BaselineAnnualPremium);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void ThinPriorCoverage_AddsThinCoverageSurcharge(int priorMonths)
    {
        var quote = TestQuotes.Clean(priorInsuranceMonths: priorMonths);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        // 900 base + 300 thin prior coverage
        Assert.Equal(1_200m, decision.BaselineAnnualPremium);
    }

    [Fact]
    public void PriorCoverageExactlySixMonths_DoesNotSurcharge()
    {
        // The rule is "< 6", so exactly 6 months is clean.
        var quote = TestQuotes.Clean(priorInsuranceMonths: 6);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(900m, decision.BaselineAnnualPremium);
    }

    [Theory]
    [InlineData("FL")]
    [InlineData("TX")]
    [InlineData("NC")]
    public void OutOfHomeState_AddsStateSurcharge(string state)
    {
        var quote = TestQuotes.Clean(state: state);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        // 900 base + 100 out-of-state
        Assert.Equal(1_000m, decision.BaselineAnnualPremium);
    }

    [Fact]
    public void HomeState_DoesNotAddStateSurcharge()
    {
        var decision = _sut.Evaluate(TestQuotes.Clean(state: "GA"), TestQuotes.Now);

        Assert.Equal(900m, decision.BaselineAnnualPremium);
    }

    [Fact]
    public void LowercaseHomeState_IsNormalisedAndNotSurcharged()
    {
        // Quote.Submit upper-cases the state; this guards that normalisation.
        var quote = TestQuotes.Clean(state: "ga");

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal("GA", quote.State);
        Assert.Equal(900m, decision.BaselineAnnualPremium);
    }

    [Fact]
    public void AllSurchargesTogether_Accumulate()
    {
        var quote = TestQuotes.Clean(
            state: "FL",
            vehicleYear: TestQuotes.CurrentYear,
            annualMileage: 20_000,
            priorInsuranceMonths: 3);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        // 900 + 175 new vehicle + 200 high mileage + 300 thin coverage + 100 out-of-state
        Assert.Equal(1_675m, decision.BaselineAnnualPremium);
    }

    [Fact]
    public void NoPriorInsurance_Refers()
    {
        var quote = TestQuotes.Clean(priorInsuranceMonths: 0);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(UnderwritingOutcome.Refer, decision.Outcome);
        Assert.Null(decision.BaselineAnnualPremium);
        Assert.Contains("prior insurance", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPriorInsurance_RefersEvenWhenEveryOtherFactorIsClean()
    {
        // Referral is an eligibility gate, not a price adjustment: it must win
        // regardless of how attractive the rest of the risk looks.
        var quote = TestQuotes.Clean(
            state: "GA",
            vehicleYear: TestQuotes.CurrentYear - 5,
            annualMileage: 4_000,
            priorInsuranceMonths: 0);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(UnderwritingOutcome.Refer, decision.Outcome);
    }

    [Fact]
    public void VehicleOlderThanProgramMaximum_IsDeclined()
    {
        var quote = TestQuotes.Clean(vehicleYear: TestQuotes.CurrentYear - 26);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(UnderwritingOutcome.Decline, decision.Outcome);
        Assert.Null(decision.BaselineAnnualPremium);
        Assert.Contains("older than", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VehicleExactlyAtProgramMaximumAge_IsStillEligible()
    {
        var quote = TestQuotes.Clean(vehicleYear: TestQuotes.CurrentYear - 25);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(UnderwritingOutcome.Eligible, decision.Outcome);
    }

    [Fact]
    public void DeclineTakesPrecedenceOverReferral()
    {
        // Both gates trip. Decline is evaluated first because an ineligible vehicle
        // is not worth an underwriter's time.
        var quote = TestQuotes.Clean(
            vehicleYear: TestQuotes.CurrentYear - 30,
            priorInsuranceMonths: 0);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Equal(UnderwritingOutcome.Decline, decision.Outcome);
    }

    [Fact]
    public void EveryDecision_CarriesAnAuditTrace()
    {
        var decision = _sut.Evaluate(TestQuotes.Clean(), TestQuotes.Now);

        Assert.NotEmpty(decision.Trace);
        Assert.Contains(decision.Trace, line => line.Contains("Base annual premium"));
        Assert.Contains(decision.Trace, line => line.Contains("Baseline annual premium"));
    }

    [Fact]
    public void TraceRecordsEachSurchargeThatFired()
    {
        var quote = TestQuotes.Clean(
            state: "TX",
            vehicleYear: TestQuotes.CurrentYear,
            annualMileage: 18_000,
            priorInsuranceMonths: 2);

        var decision = _sut.Evaluate(quote, TestQuotes.Now);

        Assert.Contains(decision.Trace, l => l.Contains("New vehicle") || l.Contains("within"));
        Assert.Contains(decision.Trace, l => l.Contains("mileage"));
        Assert.Contains(decision.Trace, l => l.Contains("Prior insurance"));
        Assert.Contains(decision.Trace, l => l.Contains("TX"));
    }

    [Fact]
    public void Evaluate_RejectsNullQuote()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Evaluate(null!, TestQuotes.Now));
    }
}
