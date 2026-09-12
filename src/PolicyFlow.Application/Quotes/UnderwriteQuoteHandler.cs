using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Domain.Rating;

namespace PolicyFlow.Application.Quotes;

/// <summary>
/// The underwriting use case: verify, decide eligibility, shop the carrier panel,
/// and publish the result.
/// </summary>
/// <remarks>
/// <para>Invoked by the worker when a <c>QuoteSubmitted</c> message arrives. The sequence is:</para>
/// <list type="number">
///   <item>Load the quote. If it is already terminal, return <c>AlreadyProcessed</c> —
///         a redelivered message must not re-rate a finished quote.</item>
///   <item>Mark it <c>Processing</c> and commit, so the UI shows progress.</item>
///   <item>Verify prior insurance against the simulated bureau.</item>
///   <item>Run eligibility. A referral or decline short-circuits the fan-out.</item>
///   <item>Fan out to all five carrier gateways concurrently.</item>
///   <item>Mark the quote <c>Rated</c> with the collected offers, or <c>Referral</c>
///         if no carrier had appetite.</item>
///   <item>Write the denormalised comparison document to the NoSQL store.</item>
/// </list>
/// <para>
/// The handler is safe to run twice. Step 1 short-circuits, and the worker's
/// <c>ProcessedMessage</c> ledger prevents the common case from reaching here.
/// </para>
/// </remarks>
public sealed class UnderwriteQuoteHandler(
    IQuoteRepository quotes,
    IUnitOfWork unitOfWork,
    ICarrierCatalog catalog,
    ICarrierRatingGateway carrierGateway,
    IQuoteComparisonStore comparisonStore,
    IPriorInsuranceVerifier verifier,
    QuoteRatingService ratingService,
    IClock clock,
    ILogger<UnderwriteQuoteHandler> logger)
{
    public async Task<UnderwritingResult> HandleAsync(Guid quoteId, CancellationToken ct = default)
    {
        var overall = Stopwatch.StartNew();

        var quote = await quotes.GetWithOffersAsync(quoteId, ct);
        if (quote is null)
        {
            logger.LogWarning(
                "Underwriting skipped: quote not found. Operation={Operation} QuoteId={QuoteId}",
                "Underwrite", quoteId);
            return UnderwritingResult.NotFound(quoteId);
        }

        var correlationId = quote.CorrelationId;

        // --- Duplicate-delivery guard. A finished quote is never re-rated.
        if (quote.IsAlreadyUnderwritten())
        {
            logger.LogInformation(
                "Underwriting skipped: quote already terminal. Operation={Operation} QuoteId={QuoteId} " +
                "QuoteNumber={QuoteNumber} Status={Status} CorrelationId={CorrelationId} Result={Result}",
                "Underwrite", quote.Id, quote.QuoteNumber, quote.Status, correlationId, "AlreadyProcessed");
            return UnderwritingResult.AlreadyProcessed(quote);
        }

        // --- Claim the quote so the UI can show Processing while carriers are shopped.
        if (quote.Status == QuoteStatus.Pending)
        {
            quote.MarkProcessing(clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);

            logger.LogInformation(
                "Quote claimed for underwriting. Operation={Operation} QuoteId={QuoteId} " +
                "QuoteNumber={QuoteNumber} CorrelationId={CorrelationId}",
                "Underwrite", quote.Id, quote.QuoteNumber, correlationId);
        }

        // --- Simulated external dependency. Throws DependencyUnavailableException
        //     for the FAIL-DEPENDENCY test customer, which the worker retries.
        var verification = await verifier.VerifyAsync(quote, ct);
        logger.LogInformation(
            "Prior insurance verified. Operation={Operation} QuoteId={QuoteId} Verified={Verified} " +
            "VerifiedMonths={VerifiedMonths} ReferenceId={ReferenceId} CorrelationId={CorrelationId} ElapsedMs={ElapsedMs}",
            "VerifyPriorInsurance", quote.Id, verification.Verified, verification.VerifiedMonths,
            verification.ReferenceId, correlationId, verification.ElapsedMs);

        // --- Eligibility. Referral and decline skip the carrier fan-out entirely.
        var decision = ratingService.Evaluate(quote, clock.UtcNow);

        if (decision.Outcome is UnderwritingOutcome.Refer)
        {
            quote.MarkReferral(decision.Reason!, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            await WriteComparisonDocumentAsync(quote, decision, [], [], overall, ct);

            logger.LogInformation(
                "Quote referred. Operation={Operation} QuoteId={QuoteId} QuoteNumber={QuoteNumber} " +
                "Reason={Reason} CorrelationId={CorrelationId} ElapsedMs={ElapsedMs} Result={Result}",
                "Underwrite", quote.Id, quote.QuoteNumber, decision.Reason, correlationId,
                overall.ElapsedMilliseconds, "Referral");
            return UnderwritingResult.Referred(quote, decision);
        }

        if (decision.Outcome is UnderwritingOutcome.Decline)
        {
            quote.MarkDecline(decision.Reason!, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            await WriteComparisonDocumentAsync(quote, decision, [], [], overall, ct);

            logger.LogInformation(
                "Quote declined. Operation={Operation} QuoteId={QuoteId} QuoteNumber={QuoteNumber} " +
                "Reason={Reason} CorrelationId={CorrelationId} ElapsedMs={ElapsedMs} Result={Result}",
                "Underwrite", quote.Id, quote.QuoteNumber, decision.Reason, correlationId,
                overall.ElapsedMilliseconds, "Declined");
            return UnderwritingResult.Declined(quote, decision);
        }

        // --- Carrier marketplace fan-out.
        var carriers = await catalog.GetActiveCarriersAsync(ct);
        var fanOut = Stopwatch.StartNew();

        // Concurrent, not sequential: five carriers averaging 400ms each would take
        // two seconds in series. Each gateway isolates its own failure, so one slow
        // or broken carrier cannot take down the panel.
        var carrierResults = await Task.WhenAll(
            carriers.Select(carrier => carrierGateway.RequestOffersAsync(quote, carrier, ct)));

        fanOut.Stop();

        var offers = carrierResults.SelectMany(r => r.Offers).ToList();
        var declinations = carrierResults
            .Where(r => r.Offers.Count == 0)
            .Select(r => new CarrierDeclination
            {
                CarrierId = r.CarrierId,
                CarrierName = r.CarrierName,
                Reason = r.DeclineReason ?? "No offer returned.",
                WasUnavailable = r.Failed
            })
            .ToList();

        foreach (var carrierResult in carrierResults)
        {
            logger.LogInformation(
                "Carrier rated. Operation={Operation} QuoteId={QuoteId} CarrierId={CarrierId} " +
                "OfferCount={OfferCount} Failed={Failed} Reason={Reason} CorrelationId={CorrelationId} ElapsedMs={ElapsedMs}",
                "RateCarrier", quote.Id, carrierResult.CarrierId, carrierResult.Offers.Count,
                carrierResult.Failed, carrierResult.DeclineReason, correlationId, carrierResult.ElapsedMs);
        }

        // --- No appetite anywhere: refer rather than fabricate an offer.
        if (offers.Count == 0)
        {
            var reason = "No carrier on the panel will write this risk automatically. " +
                         "A licensed underwriter must review this application.";
            quote.MarkReferral(reason, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            await WriteComparisonDocumentAsync(quote, decision, [], declinations, overall, ct);

            logger.LogWarning(
                "Quote referred: empty carrier panel. Operation={Operation} QuoteId={QuoteId} " +
                "QuoteNumber={QuoteNumber} DeclinationCount={DeclinationCount} CorrelationId={CorrelationId} " +
                "ElapsedMs={ElapsedMs} Result={Result}",
                "Underwrite", quote.Id, quote.QuoteNumber, declinations.Count, correlationId,
                overall.ElapsedMilliseconds, "Referral");
            return UnderwritingResult.Referred(quote, decision);
        }

        quote.MarkRated(offers, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(ct);
        await WriteComparisonDocumentAsync(quote, decision, quote.Offers.ToList(), declinations, overall, ct);

        overall.Stop();
        logger.LogInformation(
            "Quote rated. Operation={Operation} QuoteId={QuoteId} QuoteNumber={QuoteNumber} " +
            "OfferCount={OfferCount} CarrierCount={CarrierCount} DeclinationCount={DeclinationCount} " +
            "BestCarrier={BestCarrier} BestPremium={BestPremium} BaselinePremium={BaselinePremium} " +
            "CorrelationId={CorrelationId} FanOutMs={FanOutMs} ElapsedMs={ElapsedMs} Result={Result}",
            "Underwrite", quote.Id, quote.QuoteNumber, offers.Count,
            offers.Select(o => o.CarrierId).Distinct().Count(), declinations.Count,
            quote.BestCarrierId, quote.AnnualPremium, decision.BaselineAnnualPremium,
            correlationId, fanOut.ElapsedMilliseconds, overall.ElapsedMilliseconds, "Rated");

        return UnderwritingResult.Rated(quote, decision, offers.Count);
    }

    /// <summary>
    /// Writes the denormalised comparison document to the NoSQL store.
    /// </summary>
    /// <remarks>
    /// Failure here is logged but never rethrown. The relational record is the
    /// source of truth and has already committed; losing a read-model refresh must
    /// not cause the Service Bus message to be retried and the quote re-underwritten.
    /// </remarks>
    private async Task WriteComparisonDocumentAsync(
        Quote quote,
        UnderwritingDecision decision,
        IReadOnlyList<CarrierOffer> offers,
        IReadOnlyList<CarrierDeclination> declinations,
        Stopwatch elapsed,
        CancellationToken ct)
    {
        try
        {
            var carriers = await catalog.GetActiveCarriersAsync(ct);
            var byId = carriers.ToDictionary(c => c.CarrierId, StringComparer.OrdinalIgnoreCase);
            var bestPremium = offers.Count > 0 ? offers.Min(o => o.AnnualPremium) : (decimal?)null;

            var document = new QuoteComparisonDocument
            {
                QuoteId = quote.Id,
                QuoteNumber = quote.QuoteNumber,
                CustomerName = quote.CustomerName,
                State = quote.State,
                VehicleDescription = $"{quote.VehicleYear} {quote.VehicleMake} {quote.VehicleModel}",
                Status = quote.Status.ToString(),
                BaselineAnnualPremium = decision.BaselineAnnualPremium,
                Offers = offers
                    .OrderBy(o => o.AnnualPremium)
                    .Select(o => new ComparisonOffer
                    {
                        OfferId = o.Id,
                        CarrierId = o.CarrierId,
                        CarrierName = o.CarrierName,
                        Tagline = byId.TryGetValue(o.CarrierId, out var profile) ? profile.Tagline : string.Empty,
                        BrandColor = byId.TryGetValue(o.CarrierId, out var brand) ? brand.BrandColor : "#1F3C88",
                        FinancialStrengthRating = o.FinancialStrengthRating,
                        CoverageTier = o.CoverageTier.ToString(),
                        AnnualPremium = o.AnnualPremium,
                        MonthlyPremium = o.MonthlyPremium,
                        Deductible = o.Deductible,
                        LimitsSummary =
                            $"{o.BodilyInjuryPerPerson / 1000}/{o.BodilyInjuryPerAccident / 1000}/{o.PropertyDamage / 1000}",
                        BodilyInjuryPerPerson = o.BodilyInjuryPerPerson,
                        BodilyInjuryPerAccident = o.BodilyInjuryPerAccident,
                        PropertyDamage = o.PropertyDamage,
                        IncludesRoadside = o.IncludesRoadside,
                        IncludesRentalReimbursement = o.IncludesRentalReimbursement,
                        DiscountAmount = o.DiscountAmount,
                        AppliedDiscounts = SplitDiscounts(o.AppliedDiscounts),
                        RatingTrace = SplitTrace(o.RatingTrace),
                        IsBestPrice = bestPremium.HasValue && o.AnnualPremium == bestPremium.Value
                    })
                    .ToList(),
                Declinations = declinations,
                GeneratedUtc = clock.UtcNow,
                CorrelationId = quote.CorrelationId,
                TotalRatingMs = (int)elapsed.ElapsedMilliseconds
            };

            await comparisonStore.UpsertAsync(document, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Comparison read model was not updated; relational state is authoritative. " +
                "Operation={Operation} QuoteId={QuoteId} CorrelationId={CorrelationId} Result={Result}",
                "WriteComparisonDocument", quote.Id, quote.CorrelationId, "Failed");
        }
    }

    private static List<string> SplitDiscounts(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> SplitTrace(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .ToList();
}

/// <summary>Outcome of an underwriting run, returned to the worker for logging.</summary>
public sealed record UnderwritingResult
{
    public required Guid QuoteId { get; init; }
    public string? QuoteNumber { get; init; }
    public required UnderwritingResultKind Kind { get; init; }
    public string? Status { get; init; }
    public decimal? BestAnnualPremium { get; init; }
    public decimal? BaselineAnnualPremium { get; init; }
    public int OfferCount { get; init; }
    public string? Reason { get; init; }

    public static UnderwritingResult NotFound(Guid quoteId) =>
        new() { QuoteId = quoteId, Kind = UnderwritingResultKind.NotFound };

    public static UnderwritingResult AlreadyProcessed(Quote quote) =>
        new()
        {
            QuoteId = quote.Id,
            QuoteNumber = quote.QuoteNumber,
            Kind = UnderwritingResultKind.AlreadyProcessed,
            Status = quote.Status.ToString(),
            BestAnnualPremium = quote.AnnualPremium,
            OfferCount = quote.Offers.Count
        };

    public static UnderwritingResult Rated(Quote quote, UnderwritingDecision decision, int offerCount) =>
        new()
        {
            QuoteId = quote.Id,
            QuoteNumber = quote.QuoteNumber,
            Kind = UnderwritingResultKind.Rated,
            Status = quote.Status.ToString(),
            BestAnnualPremium = quote.AnnualPremium,
            BaselineAnnualPremium = decision.BaselineAnnualPremium,
            OfferCount = offerCount
        };

    public static UnderwritingResult Referred(Quote quote, UnderwritingDecision decision) =>
        new()
        {
            QuoteId = quote.Id,
            QuoteNumber = quote.QuoteNumber,
            Kind = UnderwritingResultKind.Referred,
            Status = quote.Status.ToString(),
            BaselineAnnualPremium = decision.BaselineAnnualPremium,
            Reason = quote.DecisionReason
        };

    public static UnderwritingResult Declined(Quote quote, UnderwritingDecision decision) =>
        new()
        {
            QuoteId = quote.Id,
            QuoteNumber = quote.QuoteNumber,
            Kind = UnderwritingResultKind.Declined,
            Status = quote.Status.ToString(),
            BaselineAnnualPremium = decision.BaselineAnnualPremium,
            Reason = quote.DecisionReason
        };
}

public enum UnderwritingResultKind
{
    Rated = 0,
    Referred = 1,
    Declined = 2,

    /// <summary>A redelivered message hit an already-terminal quote. Expected, not an error.</summary>
    AlreadyProcessed = 3,

    /// <summary>Quote row is gone. The message should be completed, not retried forever.</summary>
    NotFound = 4
}
