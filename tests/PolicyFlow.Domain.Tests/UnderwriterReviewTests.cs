using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Tests;

/// <summary>
/// The human side of the state machine: approve, follow up or decline a referral.
/// </summary>
/// <remarks>
/// The property that matters is that these transitions are only reachable from the
/// work queue. An underwriter must not be able to "approve" a quote that was never
/// referred, and a decided referral must not be decidable twice.
/// </remarks>
public class UnderwriterReviewTests
{
    private static readonly DateTime Referred = TestQuotes.Now.AddMinutes(1);
    private static readonly DateTime Reviewed = TestQuotes.Now.AddHours(2);
    private const string Underwriter = "j.okafor";

    private static Quote ReferredQuote()
    {
        var quote = TestQuotes.Clean(priorInsuranceMonths: 0);
        quote.MarkReferral("No prior insurance on record.", Referred);
        return quote;
    }

    // =====================================================================
    // Approve
    // =====================================================================

    [Fact]
    public void ApproveReferral_RatesTheQuoteWithTheCheapestOfferAsHeadline()
    {
        var quote = ReferredQuote();

        quote.ApproveReferral(
            [
                TestQuotes.Offer("patriot-vale", CoverageTier.Standard, 1_600m),
                TestQuotes.Offer("liberty-crest", CoverageTier.Basic, 1_150m)
            ],
            Underwriter, "Verified lapse was a relocation.", Reviewed);

        Assert.Equal(QuoteStatus.Rated, quote.Status);
        Assert.Equal(1_150m, quote.AnnualPremium);
        Assert.Equal("liberty-crest", quote.BestCarrierId);
        Assert.Equal(2, quote.Offers.Count);
        // The referral reason no longer applies once the risk is accepted.
        Assert.Null(quote.DecisionReason);
    }

    [Fact]
    public void ApproveReferral_RecordsWhoApprovedWhenAndWhy()
    {
        var quote = ReferredQuote();

        quote.ApproveReferral([TestQuotes.Offer()], "  j.okafor  ", "  Verified by phone.  ", Reviewed);

        Assert.Equal("j.okafor", quote.ReviewedBy);
        Assert.Equal(Reviewed, quote.ReviewedUtc);
        Assert.Equal("Verified by phone.", quote.ReviewNote);
        Assert.Equal(Reviewed, quote.UpdatedUtc);
    }

    [Fact]
    public void ApproveReferral_NoteIsOptional()
    {
        var quote = ReferredQuote();

        quote.ApproveReferral([TestQuotes.Offer()], Underwriter, note: "   ", Reviewed);

        Assert.Equal(QuoteStatus.Rated, quote.Status);
        Assert.Null(quote.ReviewNote);
    }

    [Fact]
    public void ApproveReferral_FromFollowUp_IsAllowed()
    {
        var quote = ReferredQuote();
        quote.RequestFollowUp(Underwriter, "Upload proof of prior insurance.", Reviewed);

        quote.ApproveReferral([TestQuotes.Offer()], Underwriter, "Proof received.", Reviewed.AddDays(1));

        Assert.Equal(QuoteStatus.Rated, quote.Status);
        Assert.Equal("Proof received.", quote.ReviewNote);
    }

    [Fact]
    public void ApproveReferral_WithNoCarrierOffers_IsRejectedAndLeavesTheReferralUntouched()
    {
        // Approval overrides the house rule, not carrier appetite. A risk nobody
        // will write cannot become a priced quote.
        var quote = ReferredQuote();

        var ex = Assert.Throws<DomainException>(
            () => quote.ApproveReferral([], Underwriter, null, Reviewed));

        Assert.Contains("cannot be approved", ex.Message);
        Assert.Equal(QuoteStatus.Referral, quote.Status);
        Assert.Null(quote.ReviewedBy);
        Assert.Equal(Referred, quote.UpdatedUtc);
    }

    [Fact]
    public void ApprovedReferral_CanBeBoundLikeAnyRatedQuote()
    {
        var quote = ReferredQuote();
        quote.ApproveReferral([TestQuotes.Offer("liberty-crest", CoverageTier.Standard, 1_300m)], Underwriter, null, Reviewed);

        var offer = quote.SelectOfferForBinding(offerId: null);

        Assert.Equal("liberty-crest", offer.CarrierId);
    }

    [Fact]
    public void ApprovedReferral_IsNotReRatedByTheWorker()
    {
        var quote = ReferredQuote();
        quote.ApproveReferral([TestQuotes.Offer()], Underwriter, null, Reviewed);

        Assert.True(quote.IsAlreadyUnderwritten());
        Assert.Throws<DomainException>(() => quote.MarkRated([TestQuotes.Offer("carrier-b")], Reviewed));
    }

    [Fact]
    public void ApproveReferral_OnAPendingQuote_IsRejected()
    {
        // The worker has not run yet. Approving would skip eligibility entirely.
        var quote = TestQuotes.Clean();

        Assert.Throws<DomainException>(
            () => quote.ApproveReferral([TestQuotes.Offer()], Underwriter, null, Reviewed));
    }

    [Fact]
    public void ApproveReferral_OnAnAutomaticallyRatedQuote_IsRejected()
    {
        var quote = TestQuotes.Clean();
        quote.MarkRated([TestQuotes.Offer()], Referred);

        Assert.Throws<DomainException>(
            () => quote.ApproveReferral([TestQuotes.Offer("carrier-b")], Underwriter, null, Reviewed));
    }

    // =====================================================================
    // Follow up
    // =====================================================================

    [Fact]
    public void RequestFollowUp_KeepsTheQuoteInTheQueueAndPublishesTheRequest()
    {
        var quote = ReferredQuote();

        quote.RequestFollowUp(Underwriter, "Upload proof of prior insurance.", Reviewed);

        Assert.Equal(QuoteStatus.FollowUp, quote.Status);
        Assert.True(quote.Status.IsAwaitingUnderwriter());
        // The request replaces the referral reason as the customer-facing message.
        Assert.Equal("Upload proof of prior insurance.", quote.DecisionReason);
        Assert.Equal("Upload proof of prior insurance.", quote.ReviewNote);
        Assert.Equal(Underwriter, quote.ReviewedBy);
        Assert.Null(quote.AnnualPremium);
    }

    [Fact]
    public void RequestFollowUp_CanBeRevised()
    {
        var quote = ReferredQuote();
        quote.RequestFollowUp(Underwriter, "Upload proof of prior insurance.", Reviewed);

        quote.RequestFollowUp("a.lindqvist", "Also confirm the garaging address.", Reviewed.AddHours(1));

        Assert.Equal(QuoteStatus.FollowUp, quote.Status);
        Assert.Equal("a.lindqvist", quote.ReviewedBy);
        Assert.Equal("Also confirm the garaging address.", quote.DecisionReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RequestFollowUp_WithoutANote_IsRejected(string note)
    {
        var quote = ReferredQuote();

        var ex = Assert.Throws<DomainException>(() => quote.RequestFollowUp(Underwriter, note, Reviewed));

        Assert.Contains("information is needed", ex.Message);
        Assert.Equal(QuoteStatus.Referral, quote.Status);
    }

    [Fact]
    public void FollowUpQuote_IsTerminalForTheWorker()
    {
        // A redelivered QuoteSubmitted message must not re-underwrite a quote an
        // underwriter is holding.
        var quote = ReferredQuote();
        quote.RequestFollowUp(Underwriter, "Upload proof of prior insurance.", Reviewed);

        Assert.True(quote.IsAlreadyUnderwritten());
        Assert.Throws<DomainException>(() => quote.MarkProcessing(Reviewed));
    }

    // =====================================================================
    // Decline
    // =====================================================================

    [Fact]
    public void DeclineReferral_ClosesTheQuoteWithTheReason()
    {
        var quote = ReferredQuote();

        quote.DeclineReferral(Underwriter, "Three at-fault losses in 24 months.", Reviewed);

        Assert.Equal(QuoteStatus.Declined, quote.Status);
        Assert.False(quote.Status.IsAwaitingUnderwriter());
        Assert.Equal("Three at-fault losses in 24 months.", quote.DecisionReason);
        Assert.Null(quote.AnnualPremium);
        Assert.Empty(quote.Offers);
    }

    [Fact]
    public void DeclineReferral_FromFollowUp_IsAllowed()
    {
        var quote = ReferredQuote();
        quote.RequestFollowUp(Underwriter, "Upload proof of prior insurance.", Reviewed);

        quote.DeclineReferral(Underwriter, "No response after 30 days.", Reviewed.AddDays(30));

        Assert.Equal(QuoteStatus.Declined, quote.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void DeclineReferral_WithoutAReason_IsRejected(string reason)
    {
        var quote = ReferredQuote();

        Assert.Throws<DomainException>(() => quote.DeclineReferral(Underwriter, reason, Reviewed));
        Assert.Equal(QuoteStatus.Referral, quote.Status);
    }

    [Fact]
    public void DeclinedReferral_CannotBeApprovedAfterwards()
    {
        var quote = ReferredQuote();
        quote.DeclineReferral(Underwriter, "Three at-fault losses in 24 months.", Reviewed);

        Assert.Throws<DomainException>(
            () => quote.ApproveReferral([TestQuotes.Offer()], "a.lindqvist", null, Reviewed.AddHours(1)));
    }

    [Fact]
    public void AutomaticallyDeclinedQuote_CannotBeDeclinedAgainByAnUnderwriter()
    {
        // Declined by the house rules never entered the queue.
        var quote = TestQuotes.Clean();
        quote.MarkDecline("Vehicle too old.", Referred);

        Assert.Throws<DomainException>(() => quote.DeclineReferral(Underwriter, "Duplicate decline.", Reviewed));
    }

    // =====================================================================
    // Shared invariants
    // =====================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EveryDecision_RequiresAnUnderwriter(string underwriter)
    {
        Assert.Throws<DomainException>(
            () => ReferredQuote().ApproveReferral([TestQuotes.Offer()], underwriter, null, Reviewed));
        Assert.Throws<DomainException>(
            () => ReferredQuote().RequestFollowUp(underwriter, "Upload proof of prior insurance.", Reviewed));
        Assert.Throws<DomainException>(
            () => ReferredQuote().DeclineReferral(underwriter, "Three at-fault losses.", Reviewed));
    }

    [Fact]
    public void NotesLongerThanTheSchemaAllows_AreRejected()
    {
        var tooLong = new string('x', Quote.MaxReviewNoteLength + 1);

        Assert.Throws<DomainException>(() => ReferredQuote().RequestFollowUp(Underwriter, tooLong, Reviewed));
        Assert.Throws<DomainException>(() => ReferredQuote().DeclineReferral(Underwriter, tooLong, Reviewed));
        Assert.Throws<DomainException>(
            () => ReferredQuote().ApproveReferral([TestQuotes.Offer()], Underwriter, tooLong, Reviewed));
    }

    [Fact]
    public void NoteAtExactlyTheLimit_IsAccepted()
    {
        var atLimit = new string('x', Quote.MaxReviewNoteLength);
        var quote = ReferredQuote();

        quote.RequestFollowUp(Underwriter, atLimit, Reviewed);

        Assert.Equal(Quote.MaxReviewNoteLength, quote.ReviewNote!.Length);
    }

    [Theory]
    [InlineData(QuoteStatus.Referral, true)]
    [InlineData(QuoteStatus.FollowUp, true)]
    [InlineData(QuoteStatus.Pending, false)]
    [InlineData(QuoteStatus.Processing, false)]
    [InlineData(QuoteStatus.Rated, false)]
    [InlineData(QuoteStatus.Declined, false)]
    public void OnlyReferralAndFollowUpAreInTheWorkQueue(QuoteStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsAwaitingUnderwriter());
    }
}
