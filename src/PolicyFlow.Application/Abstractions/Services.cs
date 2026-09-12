using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Abstractions;

/// <summary>Publishes staged outbox messages to the message broker.</summary>
public interface IEventPublisher
{
    /// <summary>
    /// Sends one message. Must throw on failure: the outbox publisher relies on the
    /// exception to leave the row unprocessed for retry. Swallowing it would lose
    /// the event permanently.
    /// </summary>
    Task PublishAsync(
        Guid messageId,
        string eventType,
        string payload,
        string correlationId,
        CancellationToken ct = default);
}

/// <summary>
/// Simulated third-party prior-insurance verification.
/// </summary>
/// <remarks>
/// Stands in for a real bureau or carrier-history service. It exists to justify
/// why underwriting is asynchronous: the API must not hold an HTTP request open
/// waiting on a slow, occasionally failing external dependency.
/// </remarks>
public interface IPriorInsuranceVerifier
{
    Task<PriorInsuranceVerification> VerifyAsync(Quote quote, CancellationToken ct = default);
}

/// <summary>Result of a prior-insurance check.</summary>
public sealed record PriorInsuranceVerification
{
    /// <summary>True when the declared prior coverage was corroborated.</summary>
    public required bool Verified { get; init; }

    /// <summary>Months the bureau confirmed, which may differ from the declaration.</summary>
    public required int VerifiedMonths { get; init; }

    /// <summary>Synthetic reference for the verification record.</summary>
    public required string ReferenceId { get; init; }

    /// <summary>Round-trip time, logged to show where latency comes from.</summary>
    public required int ElapsedMs { get; init; }
}

/// <summary>
/// Raised when the simulated verification dependency is unavailable. Distinct from
/// <see cref="Domain.Common.DomainException"/> so the worker can tell "retry this"
/// apart from "this risk is ineligible".
/// </summary>
public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Read port for the NoSQL carrier catalog. The document store owns carrier
/// pricing configuration so it can change without a relational migration or a
/// redeploy.
/// </summary>
public interface ICarrierCatalog
{
    /// <summary>All active carriers, in stable catalog order.</summary>
    Task<IReadOnlyList<CarrierProfile>> GetActiveCarriersAsync(CancellationToken ct = default);

    Task<CarrierProfile?> GetCarrierAsync(string carrierId, CancellationToken ct = default);

    /// <summary>Writes the canonical panel if the catalog is empty. Idempotent.</summary>
    Task<int> SeedAsync(CancellationToken ct = default);
}

/// <summary>
/// One carrier's gateway. Each mock carrier is treated as a separate remote
/// dependency so the fan-out has independent latency and independent failure —
/// a slow carrier must not stop the panel from returning.
/// </summary>
public interface ICarrierRatingGateway
{
    /// <summary>Requests offers from one carrier. Never throws for a declination.</summary>
    Task<CarrierQuoteResult> RequestOffersAsync(
        Quote quote,
        CarrierProfile carrier,
        CancellationToken ct = default);
}

/// <summary>Outcome of one carrier gateway call.</summary>
public sealed record CarrierQuoteResult
{
    public required string CarrierId { get; init; }

    public required string CarrierName { get; init; }

    /// <summary>Offers returned. Empty when the carrier declined or errored.</summary>
    public required IReadOnlyList<CarrierOffer> Offers { get; init; }

    /// <summary>Why the carrier made no offer. Null when offers were returned.</summary>
    public string? DeclineReason { get; init; }

    /// <summary>True when the gateway call itself failed, as opposed to declining.</summary>
    public bool Failed { get; init; }

    public required int ElapsedMs { get; init; }

    public static CarrierQuoteResult Quoted(CarrierProfile carrier, IReadOnlyList<CarrierOffer> offers, int elapsedMs) =>
        new()
        {
            CarrierId = carrier.CarrierId,
            CarrierName = carrier.DisplayName,
            Offers = offers,
            ElapsedMs = elapsedMs
        };

    public static CarrierQuoteResult Declined(CarrierProfile carrier, string reason, int elapsedMs) =>
        new()
        {
            CarrierId = carrier.CarrierId,
            CarrierName = carrier.DisplayName,
            Offers = [],
            DeclineReason = reason,
            ElapsedMs = elapsedMs
        };

    public static CarrierQuoteResult Unavailable(CarrierProfile carrier, string reason, int elapsedMs) =>
        new()
        {
            CarrierId = carrier.CarrierId,
            CarrierName = carrier.DisplayName,
            Offers = [],
            DeclineReason = reason,
            Failed = true,
            ElapsedMs = elapsedMs
        };
}

/// <summary>
/// Simulated payment gateway. Returns a synthetic reference only — no card data,
/// tokens or credentials pass through this POC at any point.
/// </summary>
public interface IPaymentGateway
{
    Task<string> AuthorizeAsync(decimal amount, string idempotencyKey, CancellationToken ct = default);
}

/// <summary>
/// Supplies the correlation id for the current operation, so a single value flows
/// from the HTTP request through SQL, Service Bus and worker logs.
/// </summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
}

/// <summary>
/// Write port for the NoSQL quote-comparison read model: the denormalised
/// document the mobile comparison screen reads in one round trip.
/// </summary>
public interface IQuoteComparisonStore
{
    Task UpsertAsync(QuoteComparisonDocument document, CancellationToken ct = default);

    Task<QuoteComparisonDocument?> GetByQuoteIdAsync(Guid quoteId, CancellationToken ct = default);

    Task<QuoteComparisonDocument?> GetByQuoteNumberAsync(string quoteNumber, CancellationToken ct = default);
}

/// <summary>
/// Denormalised comparison document. Everything the comparison screen needs lives
/// in one document: no joins, no fan-in, one read.
/// </summary>
public sealed record QuoteComparisonDocument
{
    public required Guid QuoteId { get; init; }

    public required string QuoteNumber { get; init; }

    public required string CustomerName { get; init; }

    public required string State { get; init; }

    public required string VehicleDescription { get; init; }

    public required string Status { get; init; }

    /// <summary>House reference premium from <c>QuoteRatingService</c>, for contrast.</summary>
    public decimal? BaselineAnnualPremium { get; init; }

    public required IReadOnlyList<ComparisonOffer> Offers { get; init; }

    /// <summary>Carriers that returned nothing, with the reason. Shown as "not available".</summary>
    public required IReadOnlyList<CarrierDeclination> Declinations { get; init; }

    public required DateTime GeneratedUtc { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Total wall-clock time of the carrier fan-out.</summary>
    public required int TotalRatingMs { get; init; }
}

public sealed record ComparisonOffer
{
    public required Guid OfferId { get; init; }
    public required string CarrierId { get; init; }
    public required string CarrierName { get; init; }
    public required string Tagline { get; init; }
    public required string BrandColor { get; init; }
    public required string FinancialStrengthRating { get; init; }
    public required string CoverageTier { get; init; }
    public required decimal AnnualPremium { get; init; }
    public required decimal MonthlyPremium { get; init; }
    public required decimal Deductible { get; init; }
    public required string LimitsSummary { get; init; }
    public required int BodilyInjuryPerPerson { get; init; }
    public required int BodilyInjuryPerAccident { get; init; }
    public required int PropertyDamage { get; init; }
    public required bool IncludesRoadside { get; init; }
    public required bool IncludesRentalReimbursement { get; init; }
    public required decimal DiscountAmount { get; init; }
    public required IReadOnlyList<string> AppliedDiscounts { get; init; }
    public required IReadOnlyList<string> RatingTrace { get; init; }
    /// <summary>True for the cheapest offer in the whole panel.</summary>
    public required bool IsBestPrice { get; init; }
}

public sealed record CarrierDeclination
{
    public required string CarrierId { get; init; }
    public required string CarrierName { get; init; }
    public required string Reason { get; init; }
    /// <summary>True when the gateway failed rather than the carrier declining.</summary>
    public required bool WasUnavailable { get; init; }
}
