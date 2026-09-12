using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Tests;

/// <summary>
/// Builds quotes for tests. Every rating rule depends on the current year, so
/// <see cref="Now"/> is fixed and all fixtures are expressed relative to it.
/// </summary>
internal static class TestQuotes
{
    /// <summary>Fixed "now" for every domain test. Keeps model-year rules deterministic.</summary>
    public static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public static int CurrentYear => Now.Year;

    /// <summary>
    /// A clean risk: home state, recent-but-not-new vehicle, moderate mileage,
    /// long continuous coverage. Every surcharge is off, so the baseline is $900.
    /// </summary>
    public static Quote Clean(
        string state = "GA",
        int? vehicleYear = null,
        int annualMileage = 10_000,
        int priorInsuranceMonths = 36,
        QuoteChannel channel = QuoteChannel.Modern,
        string customerName = "Alex Johnson") =>
        Quote.Submit(
            quoteNumber: "Q-2026-000001",
            customerName: customerName,
            state: state,
            // Three model years old: outside the 2-year new-vehicle window.
            vehicleYear: vehicleYear ?? CurrentYear - 3,
            vehicleMake: "Toyota",
            vehicleModel: "Camry",
            annualMileage: annualMileage,
            priorInsuranceMonths: priorInsuranceMonths,
            channel: channel,
            correlationId: "test-correlation-id",
            nowUtc: Now);

    /// <summary>Creates an offer without going through the rating services.</summary>
    public static CarrierOffer Offer(
        string carrierId = "liberty-crest",
        CoverageTier tier = CoverageTier.Standard,
        decimal annualPremium = 1_200m) =>
        CarrierOffer.Create(
            carrierId: carrierId,
            carrierName: "Test Carrier",
            coverageTier: tier,
            annualPremium: annualPremium,
            monthlyPremium: (annualPremium / 12m) + 5m,
            deductible: 500m,
            bodilyInjuryPerPerson: 50_000,
            bodilyInjuryPerAccident: 100_000,
            propertyDamage: 50_000,
            includesRoadside: true,
            includesRentalReimbursement: false,
            financialStrengthRating: "A",
            discountAmount: 0m,
            appliedDiscounts: [],
            ratingTrace: ["test offer"],
            nowUtc: Now);
}
