using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Carriers;
using PolicyFlow.Application.Quotes;
using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Tests;

/// <summary>
/// The underwriting use case: the five-carrier fan-out, the terminal-state
/// transitions, and — most importantly — that a redelivered message cannot
/// re-rate a finished quote.
/// </summary>
public class UnderwriteQuoteHandlerTests
{
    [Fact]
    public async Task CleanGeorgiaRisk_IsRatedWithTheFullPanel()
    {
        await using var harness = new TestHarness();

        var quote = await harness.SubmitAndUnderwriteAsync(state: "GA", priorInsuranceMonths: 36);

        Assert.Equal(QuoteStatus.Rated, quote.Status);
        // 5 carriers x 3 tiers.
        Assert.Equal(15, quote.Offers.Count);
        Assert.Equal(5, quote.Offers.Select(o => o.CarrierId).Distinct().Count());
    }

    [Fact]
    public async Task RatedQuote_PublishesTheCheapestOfferAsItsPremium()
    {
        await using var harness = new TestHarness();

        var quote = await harness.SubmitAndUnderwriteAsync();

        var cheapest = quote.Offers.Min(o => o.AnnualPremium);
        Assert.Equal(cheapest, quote.AnnualPremium);
        Assert.NotNull(quote.BestCarrierId);
        Assert.NotNull(quote.BestCoverageTier);
    }

    [Fact]
    public async Task Underwriting_IsDeterministic()
    {
        // Two identical submissions must price identically. Without this the demo
        // is not credible and the numbers cannot be verified by hand.
        await using var first = new TestHarness();
        await using var second = new TestHarness();

        var quoteA = await first.SubmitAndUnderwriteAsync(state: "TX", annualMileage: 14_000);
        var quoteB = await second.SubmitAndUnderwriteAsync(state: "TX", annualMileage: 14_000);

        Assert.Equal(quoteA.AnnualPremium, quoteB.AnnualPremium);
        Assert.Equal(
            quoteA.Offers.OrderBy(o => o.CarrierId).ThenBy(o => o.CoverageTier)
                .Select(o => (o.CarrierId, o.CoverageTier, o.AnnualPremium)),
            quoteB.Offers.OrderBy(o => o.CarrierId).ThenBy(o => o.CoverageTier)
                .Select(o => (o.CarrierId, o.CoverageTier, o.AnnualPremium)));
    }

    [Fact]
    public async Task NoPriorInsurance_IsReferredWithoutShoppingAnyCarrier()
    {
        // Eligibility short-circuits the fan-out: there is no point calling five
        // carriers for a risk we already know needs manual review.
        await using var harness = new TestHarness();

        var quote = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);

        Assert.Equal(QuoteStatus.Referral, quote.Status);
        Assert.Empty(quote.Offers);
        Assert.Null(quote.AnnualPremium);
        Assert.Contains("prior insurance", quote.DecisionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VehicleOlderThanTheProgramMaximum_IsDeclined()
    {
        await using var harness = new TestHarness();

        var quote = await harness.SubmitAndUnderwriteAsync(
            vehicleYear: harness.Clock.UtcNow.Year - 30);

        Assert.Equal(QuoteStatus.Declined, quote.Status);
        Assert.Empty(quote.Offers);
    }

    [Fact]
    public async Task ThinPriorCoverage_ProducesAPartialPanel()
    {
        // Carriers have different appetites, so a shorter panel is a correct
        // outcome rather than a bug. Patriot Vale is the non-standard market and
        // should still quote.
        await using var harness = new TestHarness();

        var quote = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 2);

        Assert.Equal(QuoteStatus.Rated, quote.Status);
        Assert.True(quote.Offers.Count < 15, "A thin-file risk should not get the full panel.");
        Assert.Contains("patriot-vale", quote.Offers.Select(o => o.CarrierId));
    }

    [Fact]
    public async Task StateAvailability_RemovesACarrierFromThePanel()
    {
        // BlueRidge does not write Florida.
        await using var harness = new TestHarness();

        var quote = await harness.SubmitAndUnderwriteAsync(state: "FL");

        Assert.Equal(QuoteStatus.Rated, quote.Status);
        Assert.DoesNotContain("blueridge-assurance", quote.Offers.Select(o => o.CarrierId));
    }

    [Fact]
    public async Task NoCarrierWithAppetite_IsReferredRatherThanPricedAnyway()
    {
        // A panel that comes back empty must refer, never fabricate a premium.
        // Mileage above every carrier's cap makes this reachable.
        await using var harness = new TestHarness();

        var quote = await harness.SubmitAndUnderwriteAsync(annualMileage: 95_000);

        Assert.Equal(QuoteStatus.Referral, quote.Status);
        Assert.Empty(quote.Offers);
        Assert.Contains("underwriter", quote.DecisionReason!, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // Duplicate-delivery safety. Service Bus is at-least-once, so this is the
    // behaviour that keeps the system correct rather than merely working.
    // =====================================================================

    [Fact]
    public async Task ReprocessingARatedQuote_IsANoOp()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();

        var originalPremium = quote.AnnualPremium;
        var originalOfferCount = quote.Offers.Count;
        var originalUpdatedUtc = quote.UpdatedUtc;

        // Simulates a redelivered QuoteSubmitted message.
        var result = await harness.Underwrite.HandleAsync(quote.Id);
        harness.Db.ChangeTracker.Clear();
        var reloaded = await harness.Quotes.GetWithOffersAsync(quote.Id);

        Assert.Equal(UnderwritingResultKind.AlreadyProcessed, result.Kind);
        Assert.Equal(originalPremium, reloaded!.AnnualPremium);
        Assert.Equal(originalOfferCount, reloaded.Offers.Count);
        Assert.Equal(originalUpdatedUtc, reloaded.UpdatedUtc);
    }

    [Fact]
    public async Task ReprocessingARatedQuote_DoesNotDoubleTheOfferSet()
    {
        // The failure this guards against: 15 offers becoming 30, and the
        // comparison screen showing every carrier twice.
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();

        await harness.Underwrite.HandleAsync(quote.Id);
        await harness.Underwrite.HandleAsync(quote.Id);
        harness.Db.ChangeTracker.Clear();

        var reloaded = await harness.Quotes.GetWithOffersAsync(quote.Id);
        Assert.Equal(15, reloaded!.Offers.Count);
    }

    [Fact]
    public async Task ReprocessingAReferredQuote_IsANoOp()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);

        var result = await harness.Underwrite.HandleAsync(quote.Id);

        Assert.Equal(UnderwritingResultKind.AlreadyProcessed, result.Kind);
        Assert.Equal("Referral", result.Status);
    }

    [Fact]
    public async Task Underwriting_AMissingQuote_ReportsNotFound()
    {
        // The worker dead-letters on this rather than retrying forever: a deleted
        // row is not coming back.
        await using var harness = new TestHarness();

        var result = await harness.Underwrite.HandleAsync(Guid.NewGuid());

        Assert.Equal(UnderwritingResultKind.NotFound, result.Kind);
    }

    // =====================================================================
    // External dependency behaviour
    // =====================================================================

    [Fact]
    public async Task FailDependencyTrigger_ThrowsSoTheWorkerCanRetry()
    {
        // DependencyUnavailableException is the signal the worker uses to abandon
        // the message for redelivery. A different exception type would be
        // dead-lettered instead, which would be wrong for a transient outage.
        await using var harness = new TestHarness();

        var submitted = await harness.SubmitQuote.HandleAsync(new SubmitQuoteCommand
        {
            CustomerName = SimulatedPriorInsuranceVerifier.FailureTriggerCustomerName,
            State = "GA",
            VehicleYear = 2023,
            VehicleMake = "Toyota",
            VehicleModel = "Camry",
            AnnualMileage = 12_000,
            PriorInsuranceMonths = 36
        });

        await Assert.ThrowsAsync<DependencyUnavailableException>(
            () => harness.Underwrite.HandleAsync(submitted.QuoteId));
    }

    [Fact]
    public async Task DependencyFailure_LeavesTheQuoteProcessingForRetry()
    {
        // Not left Pending and not marked failed: Processing correctly reflects
        // "claimed, not finished", and the state machine still permits the retry
        // to complete it.
        await using var harness = new TestHarness();

        var submitted = await harness.SubmitQuote.HandleAsync(new SubmitQuoteCommand
        {
            CustomerName = SimulatedPriorInsuranceVerifier.FailureTriggerCustomerName,
            State = "GA",
            VehicleYear = 2023,
            VehicleMake = "Toyota",
            VehicleModel = "Camry",
            AnnualMileage = 12_000,
            PriorInsuranceMonths = 36
        });

        await Assert.ThrowsAsync<DependencyUnavailableException>(
            () => harness.Underwrite.HandleAsync(submitted.QuoteId));

        harness.Db.ChangeTracker.Clear();
        var quote = await harness.Quotes.GetByIdAsync(submitted.QuoteId);

        Assert.Equal(QuoteStatus.Processing, quote!.Status);
        Assert.False(quote.IsAlreadyUnderwritten());
    }

    [Fact]
    public async Task CarrierGatewayFailure_MarksTheCarrierUnavailableInsteadOfFailingTheQuote()
    {
        // One broken carrier must not take down the panel. FAIL-CARRIER makes
        // every gateway throw, so the quote refers rather than erroring.
        await using var harness = new TestHarness();

        var submitted = await harness.SubmitQuote.HandleAsync(new SubmitQuoteCommand
        {
            CustomerName = SimulatedCarrierRatingGateway.FailureTriggerCustomerName,
            State = "GA",
            VehicleYear = 2023,
            VehicleMake = "Toyota",
            VehicleModel = "Camry",
            AnnualMileage = 12_000,
            PriorInsuranceMonths = 36
        });

        var result = await harness.Underwrite.HandleAsync(submitted.QuoteId);

        Assert.Equal(UnderwritingResultKind.Referred, result.Kind);

        var document = await harness.ComparisonStore.GetByQuoteIdAsync(submitted.QuoteId);
        Assert.NotNull(document);
        Assert.All(document!.Declinations, d => Assert.True(d.WasUnavailable));
    }

    // =====================================================================
    // NoSQL read model
    // =====================================================================

    [Fact]
    public async Task Underwriting_WritesTheComparisonReadModel()
    {
        await using var harness = new TestHarness();

        var quote = await harness.SubmitAndUnderwriteAsync();

        var document = await harness.ComparisonStore.GetByQuoteIdAsync(quote.Id);
        Assert.NotNull(document);
        Assert.Equal(quote.QuoteNumber, document!.QuoteNumber);
        Assert.Equal(15, document.Offers.Count);
        // Exactly one offer carries the best-price flag the UI badges.
        Assert.Single(document.Offers, o => o.IsBestPrice);
        Assert.NotNull(document.BaselineAnnualPremium);
    }

    [Fact]
    public async Task ComparisonReadModel_IsOrderedCheapestFirst()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();

        var document = await harness.ComparisonStore.GetByQuoteIdAsync(quote.Id);

        var premiums = document!.Offers.Select(o => o.AnnualPremium).ToList();
        Assert.Equal(premiums.OrderBy(p => p), premiums);
    }

    [Fact]
    public async Task ComparisonReadModel_RecordsWhyEachCarrierDeclined()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 2);

        var document = await harness.ComparisonStore.GetByQuoteIdAsync(quote.Id);

        Assert.NotEmpty(document!.Declinations);
        Assert.All(document.Declinations, d => Assert.False(string.IsNullOrWhiteSpace(d.Reason)));
        Assert.All(document.Declinations, d => Assert.False(d.WasUnavailable));
    }

    [Fact]
    public async Task ReadModelWriteFailure_DoesNotFailUnderwriting()
    {
        // The relational record is the source of truth and has already committed.
        // Throwing here would cause the message to be retried and the quote
        // re-underwritten — trading a cache miss for real data corruption.
        await using var harness = new TestHarness();
        harness.ComparisonStore.FailWrites = true;

        var quote = await harness.SubmitAndUnderwriteAsync();

        Assert.Equal(QuoteStatus.Rated, quote.Status);
        Assert.Equal(15, quote.Offers.Count);
    }

    [Fact]
    public async Task GetOffers_FallsBackToSqlWhenTheReadModelIsUnavailable()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();

        harness.ComparisonStore.FailReads = true;
        var comparison = await harness.GetOffers.HandleAsync(quote.Id);

        Assert.NotNull(comparison);
        Assert.Equal(15, comparison!.Offers.Count);
        Assert.Equal(quote.QuoteNumber, comparison.QuoteNumber);
    }

    [Fact]
    public async Task GetOffers_ReturnsNullForAnUnknownQuote()
    {
        await using var harness = new TestHarness();

        Assert.Null(await harness.GetOffers.HandleAsync(Guid.NewGuid()));
    }

    // =====================================================================
    // Catalog-driven pricing
    // =====================================================================

    [Fact]
    public async Task PanelSize_FollowsTheCatalog()
    {
        // Pricing configuration comes from the document store, so reducing the
        // catalog must reduce the panel — proving the catalog is really the input
        // rather than a decorative lookup.
        var twoCarriers = CarrierPanel.All.Take(2).ToList();
        await using var harness = new TestHarness(twoCarriers);

        var quote = await harness.SubmitAndUnderwriteAsync();

        Assert.Equal(6, quote.Offers.Count);
        Assert.Equal(2, quote.Offers.Select(o => o.CarrierId).Distinct().Count());
    }

    [Fact]
    public async Task CatalogPricingChange_ChangesThePremium()
    {
        // An operator edits the catalog; the next quote prices differently with no
        // redeploy. That is the reason carrier configuration lives in MongoDB.
        var carrier = CarrierPanel.All.First() with { BaseAnnualPremium = 2_000m };
        await using var harness = new TestHarness([carrier]);

        var quote = await harness.SubmitAndUnderwriteAsync();

        await using var baseline = new TestHarness([CarrierPanel.All.First()]);
        var baselineQuote = await baseline.SubmitAndUnderwriteAsync();

        Assert.True(quote.AnnualPremium > baselineQuote.AnnualPremium);
    }
}
