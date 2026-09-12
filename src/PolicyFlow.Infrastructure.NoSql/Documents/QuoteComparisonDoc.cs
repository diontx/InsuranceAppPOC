using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PolicyFlow.Infrastructure.NoSql.Documents;

/// <summary>
/// The denormalised carrier-comparison read model, one document per quote.
/// </summary>
/// <remarks>
/// <para>
/// The comparison screen needs, for up to fifteen offers: premium, coverage
/// limits, deductible, discount list, rating trace, carrier branding and a
/// declination list. Relationally that is a join across quote, offer and carrier
/// plus a computed "carriers that did not quote" set — several round trips, on the
/// screen a customer is most likely to open on a phone.
/// </para>
/// <para>
/// Here it is one document read by <c>QuoteId</c>: one index seek, one payload,
/// already in the shape the UI renders.
/// </para>
/// <para>
/// <b>This is a cache, not the source of truth.</b> The relational rows are
/// authoritative. If this document is missing or stale, <c>GetQuoteOffersHandler</c>
/// projects from SQL instead, and the underwriting handler treats a failed write
/// here as non-fatal — losing a read-model refresh must not cause a quote to be
/// re-underwritten.
/// </para>
/// </remarks>
[BsonIgnoreExtraElements]
public sealed class QuoteComparisonDoc
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>Primary lookup key. Uniquely indexed.</summary>
    public Guid QuoteId { get; set; }

    /// <summary>Secondary lookup key for the mobile quote-number search.</summary>
    public string QuoteNumber { get; set; } = null!;

    public string CustomerName { get; set; } = null!;

    public string State { get; set; } = null!;

    public string VehicleDescription { get; set; } = null!;

    public string Status { get; set; } = null!;

    /// <summary>House reference premium, shown beside the carrier panel for contrast.</summary>
    public double? BaselineAnnualPremium { get; set; }

    public List<ComparisonOfferSubDocument> Offers { get; set; } = [];

    /// <summary>Carriers that returned no offer, with the reason.</summary>
    public List<DeclinationSubDocument> Declinations { get; set; } = [];

    public DateTime GeneratedUtc { get; set; }

    public string CorrelationId { get; set; } = null!;

    /// <summary>Wall-clock milliseconds for the whole underwriting run.</summary>
    public int TotalRatingMs { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class ComparisonOfferSubDocument
{
    public Guid OfferId { get; set; }
    public string CarrierId { get; set; } = null!;
    public string CarrierName { get; set; } = null!;
    public string Tagline { get; set; } = null!;
    public string BrandColor { get; set; } = null!;
    public string FinancialStrengthRating { get; set; } = null!;
    public string CoverageTier { get; set; } = null!;

    /// <summary>
    /// Double in BSON. The authoritative cent-exact value lives in
    /// <c>CarrierOffers.AnnualPremium</c> as <c>decimal(18,2)</c>; this copy is for
    /// display and is re-read from SQL before anything binds or charges.
    /// </summary>
    public double AnnualPremium { get; set; }

    public double MonthlyPremium { get; set; }
    public double Deductible { get; set; }
    public string LimitsSummary { get; set; } = null!;
    public int BodilyInjuryPerPerson { get; set; }
    public int BodilyInjuryPerAccident { get; set; }
    public int PropertyDamage { get; set; }
    public bool IncludesRoadside { get; set; }
    public bool IncludesRentalReimbursement { get; set; }
    public double DiscountAmount { get; set; }
    public List<string> AppliedDiscounts { get; set; } = [];

    /// <summary>Step-by-step rating explanation, shown in an expandable panel.</summary>
    public List<string> RatingTrace { get; set; } = [];

    public bool IsBestPrice { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class DeclinationSubDocument
{
    public string CarrierId { get; set; } = null!;
    public string CarrierName { get; set; } = null!;
    public string Reason { get; set; } = null!;

    /// <summary>True when the gateway failed rather than the carrier declining.</summary>
    public bool WasUnavailable { get; set; }
}
