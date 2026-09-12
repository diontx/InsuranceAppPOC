using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PolicyFlow.Infrastructure.NoSql.Documents;

/// <summary>
/// A carrier's rating configuration as a MongoDB document.
/// </summary>
/// <remarks>
/// <para>
/// This is the case for a document store rather than another relational table.
/// Carrier configuration is deeply nested and <i>ragged</i>: every carrier has a
/// different set of state factors, a different discount list, and may file
/// different coverage tiers. Modelling that relationally needs several child
/// tables and a join per read, and adding a carrier-specific factor means a
/// schema migration. Here, one document is one carrier, read in one round trip,
/// and a new factor is a new field.
/// </para>
/// <para>
/// It is also read-mostly and non-transactional. Nothing about pricing
/// configuration needs to participate in the ACID transaction that binds a
/// policy, so it does not belong in the relational store that provides one.
/// </para>
/// <para>
/// EF Core manages these documents through the official
/// <c>MongoDB.EntityFrameworkCore</c> provider, so the same <c>DbContext</c>
/// programming model, change tracking and LINQ apply to both stores.
/// </para>
/// <para>
/// <b>Shape constraint.</b> Nested collections are <c>List&lt;T&gt;</c> of owned
/// entity types, not <c>Dictionary&lt;string, T&gt;</c>. The EF Mongo provider
/// treats a dictionary with a complex value type as an unresolvable navigation
/// ("Unable to determine the relationship represented by navigation ... of type
/// Dictionary&lt;string, CoverageSubDocument&gt;"). Dictionaries with
/// <i>primitive</i> values map cleanly, which is why
/// <see cref="StateFactors"/> and <see cref="TierFactors"/> stay dictionaries
/// while <see cref="Coverages"/> carries its key as a field.
/// </para>
/// </remarks>
[BsonIgnoreExtraElements]
public sealed class CarrierDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>Stable catalog key, e.g. <c>liberty-crest</c>. Uniquely indexed.</summary>
    public string CarrierId { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string Tagline { get; set; } = null!;

    public string BrandColor { get; set; } = null!;

    public string FinancialStrengthRating { get; set; } = null!;

    /// <summary>
    /// Stored as double because BSON has no decimal128 mapping in this provider
    /// version. Acceptable for <i>configuration</i>: these factors are inputs to
    /// a calculation whose output is rounded to cents in the relational store,
    /// which is where money is actually held as <c>decimal(18,2)</c>.
    /// </summary>
    public double BaseAnnualPremium { get; set; }

    public double NewVehicleFactor { get; set; }

    public double HighMileageFactor { get; set; }

    public double ThinPriorCoverageFactor { get; set; }

    /// <summary>
    /// State code to rating multiplier. A state absent from this map means the
    /// carrier is not licensed there. Primitive values, so it maps as a dictionary.
    /// </summary>
    public Dictionary<string, double> StateFactors { get; set; } = [];

    /// <summary>Coverage tier name to premium multiplier.</summary>
    public Dictionary<string, double> TierFactors { get; set; } = [];

    /// <summary>
    /// Limits and deductible per tier. A list rather than a dictionary because the
    /// value is a complex type; <see cref="CoverageSubDocument.Tier"/> carries
    /// what would otherwise be the key.
    /// </summary>
    public List<CoverageSubDocument> Coverages { get; set; } = [];

    public List<DiscountSubDocument> Discounts { get; set; } = [];

    public double MonthlyInstalmentFee { get; set; }

    public int MinimumPriorInsuranceMonths { get; set; }

    public int MaximumAnnualMileage { get; set; }

    public int SimulatedLatencyMsMin { get; set; }

    public int SimulatedLatencyMsMax { get; set; }

    /// <summary>
    /// Appetite switch. A carrier can be pulled off the panel without deleting
    /// its historical configuration.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Stable ordering so seeded data and tests do not drift.</summary>
    public int SortOrder { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Limits and features for one coverage tier.</summary>
[BsonIgnoreExtraElements]
public sealed class CoverageSubDocument
{
    /// <summary>Coverage tier name. The key this sub-document is looked up by.</summary>
    public string Tier { get; set; } = null!;

    public double Deductible { get; set; }
    public int BodilyInjuryPerPerson { get; set; }
    public int BodilyInjuryPerAccident { get; set; }
    public int PropertyDamage { get; set; }
    public bool IncludesRoadside { get; set; }
    public bool IncludesRentalReimbursement { get; set; }
}

/// <summary>A conditional premium reduction.</summary>
[BsonIgnoreExtraElements]
public sealed class DiscountSubDocument
{
    public string Name { get; set; } = null!;

    /// <summary>Matches a <c>DiscountKind</c> enum name.</summary>
    public string Kind { get; set; } = null!;

    public double Percentage { get; set; }

    public int Threshold { get; set; }
}
