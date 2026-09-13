using PolicyFlow.Application.Policies;
using PolicyFlow.Application.Quotes;
using PolicyFlow.Application.Underwriting;
using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Domain.Rating;

namespace PolicyFlow.Application.Tests;

/// <summary>
/// The underwriter work queue and decisions: what the queue contains, what the
/// review screen shows, and what each decision does to the quote.
/// </summary>
/// <remarks>
/// The single most important assertion here is
/// <see cref="Approve_PricesExactlyWhatTheCarrierRatingServiceProduces"/>: an
/// approved referral must be priced by the same rating code as an automatically
/// rated quote, or the underwriting channel becomes a second, drifting copy of the
/// pricing rules.
/// </remarks>
public class UnderwritingReviewHandlerTests
{
    private const string Underwriter = "j.okafor";

    private static ReviewReferralCommand Decision(
        Guid quoteId,
        ReviewDecision decision,
        string? note = null,
        string underwriter = Underwriter) => new()
        {
            QuoteId = quoteId,
            Decision = decision,
            Underwriter = underwriter,
            Note = note
        };

    private static async Task<Quote> ReloadAsync(TestHarness harness, Guid quoteId)
    {
        harness.Db.ChangeTracker.Clear();
        return (await harness.Quotes.GetWithOffersAsync(quoteId))!;
    }

    // =====================================================================
    // Queue
    // =====================================================================

    [Fact]
    public async Task Queue_ContainsOnlyReferralsAndFollowUps_OldestFirst()
    {
        await using var harness = new TestHarness();

        var rated = await harness.SubmitAndUnderwriteAsync(customerName: "Rated Customer");
        var declined = await harness.SubmitAndUnderwriteAsync(
            customerName: "Declined Customer", vehicleYear: harness.Clock.UtcNow.Year - 30);

        var olderReferral = await harness.SubmitAndUnderwriteAsync(
            customerName: "Older Referral", priorInsuranceMonths: 0);
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        var newerReferral = await harness.SubmitAndUnderwriteAsync(
            customerName: "Newer Referral", annualMileage: 95_000);
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        var followUp = await harness.SubmitAndUnderwriteAsync(
            customerName: "Follow Up", priorInsuranceMonths: 0);
        await harness.ReviewReferral.HandleAsync(
            Decision(followUp.Id, ReviewDecision.FollowUp, "Upload proof of prior insurance."));

        var queue = await harness.UnderwritingQueue.HandleAsync();

        Assert.Equal(
            [olderReferral.Id, newerReferral.Id, followUp.Id],
            queue.Items.Select(i => i.QuoteId));
        Assert.DoesNotContain(queue.Items, i => i.QuoteId == rated.Id || i.QuoteId == declined.Id);
        Assert.Equal(2, queue.ReferralCount);
        Assert.Equal(1, queue.FollowUpCount);
        Assert.False(queue.IsTruncated);
    }

    [Fact]
    public async Task QueueItem_CarriesEnoughToTriageWithoutOpeningTheQuote()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(
            customerName: "Dana Reyes", state: "TX", priorInsuranceMonths: 0,
            channel: QuoteChannel.LegacyWebForms);

        var item = Assert.Single((await harness.UnderwritingQueue.HandleAsync()).Items);

        Assert.Equal(referral.QuoteNumber, item.QuoteNumber);
        Assert.Equal("Dana Reyes", item.CustomerName);
        Assert.Equal("TX", item.State);
        Assert.Equal("Referral", item.Status);
        Assert.Equal("LegacyWebForms", item.Channel);
        Assert.Contains("prior insurance", item.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyQueue_IsAnEmptyListNotAnError()
    {
        await using var harness = new TestHarness();

        var queue = await harness.UnderwritingQueue.HandleAsync();

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.ReferralCount);
    }

    // =====================================================================
    // Review detail
    // =====================================================================

    [Fact]
    public async Task Review_ShowsTheEligibilityRuleThatCausedTheReferral()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);

        var review = await harness.UnderwritingReview.HandleAsync(referral.Id);

        Assert.NotNull(review);
        Assert.True(review!.IsAwaitingReview);
        Assert.Equal("Refer", review.EligibilityOutcome);
        Assert.Null(review.BaselineAnnualPremium);
        Assert.Contains(review.EligibilityTrace, line => line.StartsWith("REFER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Review_ShowsWhichCarriersWouldWriteTheRisk()
    {
        // Zero months of prior coverage: Summit Shield (6) and Ironclad (12) have a
        // minimum; the other three do not. The underwriter should see that before
        // pressing Approve.
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(state: "GA", priorInsuranceMonths: 0);

        var review = (await harness.UnderwritingReview.HandleAsync(referral.Id))!;

        Assert.Equal(5, review.CarrierAppetite.Count);
        Assert.Equal(
            ["blueridge-assurance", "liberty-crest", "patriot-vale"],
            review.CarrierAppetite.Where(c => c.WillWrite).Select(c => c.CarrierId).Order());

        var summit = review.CarrierAppetite.Single(c => c.CarrierId == "summit-shield");
        Assert.False(summit.WillWrite);
        Assert.Contains("prior coverage", summit.Reason!);
        Assert.All(review.CarrierAppetite.Where(c => c.WillWrite), c => Assert.Null(c.Reason));
    }

    [Fact]
    public async Task Review_OfAQuoteNoCarrierWillWrite_ShowsTheHouseBaselineAndAnEmptyAppetite()
    {
        // Referred by the panel, not the house rules: eligibility passed and priced a
        // baseline, but every carrier's mileage cap excludes it.
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(annualMileage: 95_000);

        var review = (await harness.UnderwritingReview.HandleAsync(referral.Id))!;

        Assert.Equal("Eligible", review.EligibilityOutcome);
        Assert.NotNull(review.BaselineAnnualPremium);
        Assert.DoesNotContain(review.CarrierAppetite, c => c.WillWrite);
    }

    [Fact]
    public async Task Review_OfAnUnknownQuote_IsNull()
    {
        await using var harness = new TestHarness();

        Assert.Null(await harness.UnderwritingReview.HandleAsync(Guid.NewGuid()));
    }

    // =====================================================================
    // Approve
    // =====================================================================

    [Fact]
    public async Task Approve_RatesTheReferralThroughTheCarrierPanel()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(state: "GA", priorInsuranceMonths: 0);

        var review = await harness.ReviewReferral.HandleAsync(
            Decision(referral.Id, ReviewDecision.Approve, "Lapse explained by overseas posting."));

        var quote = await ReloadAsync(harness, referral.Id);
        Assert.Equal(QuoteStatus.Rated, quote.Status);
        // Three carriers in appetite x three tiers.
        Assert.Equal(9, quote.Offers.Count);
        Assert.Equal(quote.Offers.Min(o => o.AnnualPremium), quote.AnnualPremium);

        Assert.Equal("Rated", review.Status);
        Assert.False(review.IsAwaitingReview);
        Assert.Equal(9, review.OfferCount);
        Assert.Equal(Underwriter, review.ReviewedBy);
        Assert.Equal("Lapse explained by overseas posting.", review.ReviewNote);
        Assert.Equal(harness.Clock.UtcNow, review.ReviewedUtc);
    }

    [Fact]
    public async Task Approve_PricesExactlyWhatTheCarrierRatingServiceProduces()
    {
        // No second pricing path: every approved offer must equal what the domain
        // rating service computes for that carrier and tier.
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(state: "TX", priorInsuranceMonths: 0);

        await harness.ReviewReferral.HandleAsync(Decision(referral.Id, ReviewDecision.Approve));
        var quote = await ReloadAsync(harness, referral.Id);

        var rating = new CarrierRatingService();
        var expected = CarrierPanel.All
            .SelectMany(carrier => rating.RateCarrier(quote, carrier, harness.Clock.UtcNow))
            .Select(o => (o.CarrierId, o.CoverageTier, o.AnnualPremium))
            .OrderBy(o => o.CarrierId).ThenBy(o => o.CoverageTier);

        var actual = quote.Offers
            .Select(o => (o.CarrierId, o.CoverageTier, o.AnnualPremium))
            .OrderBy(o => o.CarrierId).ThenBy(o => o.CoverageTier);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Approve_RefreshesTheComparisonReadModel()
    {
        // The referral wrote a zero-offer document. If approval did not replace it,
        // the customer's comparison screen would show no offers for a rated quote.
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);
        Assert.Empty((await harness.ComparisonStore.GetByQuoteIdAsync(referral.Id))!.Offers);

        await harness.ReviewReferral.HandleAsync(Decision(referral.Id, ReviewDecision.Approve));

        var document = await harness.GetOffers.HandleAsync(referral.Id);
        Assert.Equal("Rated", document!.Status);
        Assert.Equal(9, document.Offers.Count);
        Assert.Single(document.Offers, o => o.IsBestPrice);
        Assert.Contains(document.Declinations, d => d.CarrierId == "ironclad-direct");
    }

    [Fact]
    public async Task Approve_ReadModelFailure_DoesNotUndoTheCommittedApproval()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);
        harness.ComparisonStore.FailWrites = true;

        var review = await harness.ReviewReferral.HandleAsync(Decision(referral.Id, ReviewDecision.Approve));

        Assert.Equal("Rated", review.Status);
        Assert.Equal(QuoteStatus.Rated, (await ReloadAsync(harness, referral.Id)).Status);
    }

    [Fact]
    public async Task Approve_WhenNoCarrierWillWriteTheRisk_IsABusinessRuleViolationAndChangesNothing()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(annualMileage: 95_000);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => harness.ReviewReferral.HandleAsync(Decision(referral.Id, ReviewDecision.Approve)));

        // 422, not 409: nothing conflicted; the request cannot succeed on this risk.
        Assert.IsNotType<BusinessConflictException>(ex);
        var quote = await ReloadAsync(harness, referral.Id);
        Assert.Equal(QuoteStatus.Referral, quote.Status);
        Assert.Null(quote.ReviewedBy);
        Assert.Empty(quote.Offers);
    }

    [Fact]
    public async Task ApprovedReferral_CanBeBoundIntoAPolicy()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);
        await harness.ReviewReferral.HandleAsync(Decision(referral.Id, ReviewDecision.Approve));
        harness.Db.ChangeTracker.Clear();

        var policy = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = referral.Id });

        Assert.Equal(referral.QuoteNumber, policy.QuoteNumber);
        Assert.Equal("Active", policy.Status);
    }

    // =====================================================================
    // Follow up and decline
    // =====================================================================

    [Fact]
    public async Task FollowUp_KeepsTheQuoteInTheQueueWithTheRequestVisibleToTheCustomer()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);

        var review = await harness.ReviewReferral.HandleAsync(
            Decision(referral.Id, ReviewDecision.FollowUp, "Upload proof of prior insurance."));

        Assert.Equal("FollowUp", review.Status);
        Assert.True(review.IsAwaitingReview);

        var status = await harness.GetQuote.HandleAsync(referral.Id);
        Assert.Equal("FollowUp", status!.Status);
        Assert.True(status.IsTerminal);
        Assert.Equal("Upload proof of prior insurance.", status.DecisionReason);

        Assert.Single((await harness.UnderwritingQueue.HandleAsync()).Items, i => i.Status == "FollowUp");
    }

    [Fact]
    public async Task FollowUpQuote_RedeliveredToTheWorker_IsANoOp()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);
        await harness.ReviewReferral.HandleAsync(
            Decision(referral.Id, ReviewDecision.FollowUp, "Upload proof of prior insurance."));
        harness.Db.ChangeTracker.Clear();

        var result = await harness.Underwrite.HandleAsync(referral.Id);

        Assert.Equal(UnderwritingResultKind.AlreadyProcessed, result.Kind);
        Assert.Equal("FollowUp", result.Status);
    }

    [Fact]
    public async Task Decline_RemovesTheQuoteFromTheQueue()
    {
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);

        var review = await harness.ReviewReferral.HandleAsync(
            Decision(referral.Id, ReviewDecision.Decline, "Unable to verify any driving history."));

        Assert.Equal("Declined", review.Status);
        Assert.Equal("Unable to verify any driving history.", review.DecisionReason);
        Assert.Empty((await harness.UnderwritingQueue.HandleAsync()).Items);
    }

    // =====================================================================
    // Conflicts
    // =====================================================================

    [Fact]
    public async Task DecidingAQuoteThatWasAlreadyDecided_IsAConflict()
    {
        // The second underwriter had the quote open while the first declined it.
        await using var harness = new TestHarness();
        var referral = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);
        await harness.ReviewReferral.HandleAsync(
            Decision(referral.Id, ReviewDecision.Decline, "Unable to verify any driving history."));
        harness.Db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<BusinessConflictException>(
            () => harness.ReviewReferral.HandleAsync(
                Decision(referral.Id, ReviewDecision.Approve, underwriter: "a.lindqvist")));

        Assert.Contains("no longer awaiting underwriter review", ex.Message);
        Assert.Equal(QuoteStatus.Declined, (await ReloadAsync(harness, referral.Id)).Status);
    }

    [Fact]
    public async Task DecidingAQuoteThatWasNeverReferred_IsAConflict()
    {
        await using var harness = new TestHarness();
        var rated = await harness.SubmitAndUnderwriteAsync();

        await Assert.ThrowsAsync<BusinessConflictException>(
            () => harness.ReviewReferral.HandleAsync(
                Decision(rated.Id, ReviewDecision.Decline, "Should not be possible.")));
    }

    [Fact]
    public async Task DecidingAnUnknownQuote_IsNotFound()
    {
        await using var harness = new TestHarness();

        await Assert.ThrowsAsync<QuoteNotFoundException>(
            () => harness.ReviewReferral.HandleAsync(Decision(Guid.NewGuid(), ReviewDecision.Approve)));
    }

    // =====================================================================
    // Validation
    // =====================================================================

    [Theory]
    [InlineData(ReviewDecision.Approve, null, true)]
    [InlineData(ReviewDecision.Approve, "", true)]
    [InlineData(ReviewDecision.FollowUp, null, false)]
    [InlineData(ReviewDecision.FollowUp, "   ", false)]
    [InlineData(ReviewDecision.FollowUp, "too short", false)]
    [InlineData(ReviewDecision.FollowUp, "Send proof.", true)]
    [InlineData(ReviewDecision.Decline, null, false)]
    [InlineData(ReviewDecision.Decline, "Unverifiable history.", true)]
    public void Validator_RequiresAMeaningfulNoteForFollowUpAndDecline(
        ReviewDecision decision, string? note, bool expectedValid)
    {
        var result = new ReviewReferralCommandValidator().Validate(Decision(Guid.NewGuid(), decision, note));

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(result.Errors, e => e.PropertyName == nameof(ReviewReferralCommand.Note));
        }
    }

    [Fact]
    public void Validator_RejectsANoteLongerThanTheSchemaAllows()
    {
        var result = new ReviewReferralCommandValidator().Validate(
            Decision(Guid.NewGuid(), ReviewDecision.Approve, new string('x', Quote.MaxReviewNoteLength + 1)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validator_RejectsAnUndefinedDecision()
    {
        var result = new ReviewReferralCommandValidator().Validate(
            Decision(Guid.NewGuid(), (ReviewDecision)42, "Some note here."));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ReviewReferralCommand.Decision));
    }
}
