using PolicyFlow.Domain.Common;

namespace PolicyFlow.Domain.Quotes;

/// <summary>
/// One priced coverage option returned by one mock carrier for one quote. A rated
/// quote normally carries fifteen of these: five carriers times three coverage
/// tiers, minus any the carrier declined to write.
/// </summary>
public class CarrierOffer
{
    private CarrierOffer() { }

    public Guid Id { get; private set; }

    public Guid QuoteId { get; private set; }

    /// <summary>Stable catalog key, e.g. <c>liberty-crest</c>.</summary>
    public string CarrierId { get; private set; } = null!;

    public string CarrierName { get; private set; } = null!;

    public CoverageTier CoverageTier { get; private set; }

    /// <summary>Total annual premium for this carrier and tier, rounded to cents.</summary>
    public decimal AnnualPremium { get; private set; }

    /// <summary>
    /// Monthly instalment including the carrier's instalment fee. Stored rather
    /// than derived because carriers apply different instalment surcharges.
    /// </summary>
    public decimal MonthlyPremium { get; private set; }

    public decimal Deductible { get; private set; }

    /// <summary>Bodily injury limit per person, in dollars.</summary>
    public int BodilyInjuryPerPerson { get; private set; }

    /// <summary>Bodily injury limit per accident, in dollars.</summary>
    public int BodilyInjuryPerAccident { get; private set; }

    /// <summary>Property damage limit per accident, in dollars.</summary>
    public int PropertyDamage { get; private set; }

    public bool IncludesRoadside { get; private set; }

    public bool IncludesRentalReimbursement { get; private set; }

    /// <summary>A.M.-Best-style financial strength grade shown in the UI, e.g. <c>A+</c>.</summary>
    public string FinancialStrengthRating { get; private set; } = null!;

    /// <summary>Total dollars removed by discounts, for the savings badge.</summary>
    public decimal DiscountAmount { get; private set; }

    /// <summary>Semicolon-separated discount names that were applied.</summary>
    public string AppliedDiscounts { get; private set; } = string.Empty;

    /// <summary>
    /// Auditable breakdown of every rating step, newline separated. The demo shows
    /// this to prove pricing is deterministic rather than random.
    /// </summary>
    public string RatingTrace { get; private set; } = string.Empty;

    public DateTime CreatedUtc { get; private set; }

    public Quote? Quote { get; private set; }

    public static CarrierOffer Create(
        string carrierId,
        string carrierName,
        CoverageTier coverageTier,
        decimal annualPremium,
        decimal monthlyPremium,
        decimal deductible,
        int bodilyInjuryPerPerson,
        int bodilyInjuryPerAccident,
        int propertyDamage,
        bool includesRoadside,
        bool includesRentalReimbursement,
        string financialStrengthRating,
        decimal discountAmount,
        IEnumerable<string> appliedDiscounts,
        IEnumerable<string> ratingTrace,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(carrierId))
        {
            throw new DomainException("Carrier id is required on an offer.");
        }

        if (annualPremium <= 0)
        {
            throw new DomainException("Offer premium must be greater than zero.");
        }

        return new CarrierOffer
        {
            Id = Guid.NewGuid(),
            CarrierId = carrierId,
            CarrierName = carrierName,
            CoverageTier = coverageTier,
            AnnualPremium = decimal.Round(annualPremium, 2, MidpointRounding.AwayFromZero),
            MonthlyPremium = decimal.Round(monthlyPremium, 2, MidpointRounding.AwayFromZero),
            Deductible = deductible,
            BodilyInjuryPerPerson = bodilyInjuryPerPerson,
            BodilyInjuryPerAccident = bodilyInjuryPerAccident,
            PropertyDamage = propertyDamage,
            IncludesRoadside = includesRoadside,
            IncludesRentalReimbursement = includesRentalReimbursement,
            FinancialStrengthRating = financialStrengthRating,
            DiscountAmount = decimal.Round(discountAmount, 2, MidpointRounding.AwayFromZero),
            AppliedDiscounts = string.Join("; ", appliedDiscounts),
            RatingTrace = string.Join(Environment.NewLine, ratingTrace),
            CreatedUtc = nowUtc
        };
    }

    /// <summary>
    /// Associates the offer with its parent quote. Called by
    /// <see cref="Quote.MarkRated"/> so the foreign key is never set externally.
    /// </summary>
    internal void AttachTo(Guid quoteId) => QuoteId = quoteId;
}
