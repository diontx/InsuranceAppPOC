using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Tests;

/// <summary>
/// Guards the quote state machine. These tests are the reason a redelivered
/// Service Bus message cannot silently re-rate a finished quote.
/// </summary>
public class QuoteStateTransitionTests
{
    private static readonly DateTime Later = TestQuotes.Now.AddMinutes(5);

    [Fact]
    public void NewQuote_StartsPendingWithNoPremium()
    {
        var quote = TestQuotes.Clean();

        Assert.Equal(QuoteStatus.Pending, quote.Status);
        Assert.Null(quote.AnnualPremium);
        Assert.Null(quote.BestCarrierId);
        Assert.Empty(quote.Offers);
        Assert.False(quote.IsAlreadyUnderwritten());
    }

    [Fact]
    public void PendingToProcessing_IsAllowed()
    {
        var quote = TestQuotes.Clean();

        quote.MarkProcessing(Later);

        Assert.Equal(QuoteStatus.Processing, quote.Status);
        Assert.Equal(Later, quote.UpdatedUtc);
    }

    [Fact]
    public void ProcessingToProcessing_IsRejected()
    {
        // A second delivery must not re-claim a quote already in flight.
        var quote = TestQuotes.Clean();
        quote.MarkProcessing(Later);

        var ex = Assert.Throws<DomainException>(() => quote.MarkProcessing(Later));
        Assert.Contains("Processing", ex.Message);
    }

    [Fact]
    public void ProcessingToRated_PublishesCheapestOfferAsHeadlinePremium()
    {
        var quote = TestQuotes.Clean();
        quote.MarkProcessing(Later);

        quote.MarkRated(
            [
                TestQuotes.Offer("carrier-a", CoverageTier.Standard, 1_500m),
                TestQuotes.Offer("carrier-b", CoverageTier.Basic, 1_100m),
                TestQuotes.Offer("carrier-c", CoverageTier.Premium, 1_900m)
            ],
            Later);

        Assert.Equal(QuoteStatus.Rated, quote.Status);
        Assert.Equal(1_100m, quote.AnnualPremium);
        Assert.Equal("carrier-b", quote.BestCarrierId);
        Assert.Equal(CoverageTier.Basic, quote.BestCoverageTier);
        Assert.True(quote.IsAlreadyUnderwritten());
    }

    [Fact]
    public void RatedQuote_StoresOffersCheapestFirst()
    {
        var quote = TestQuotes.Clean();

        quote.MarkRated(
            [
                TestQuotes.Offer("carrier-a", CoverageTier.Premium, 2_000m),
                TestQuotes.Offer("carrier-b", CoverageTier.Basic, 900m),
                TestQuotes.Offer("carrier-c", CoverageTier.Standard, 1_400m)
            ],
            Later);

        var premiums = quote.Offers.Select(o => o.AnnualPremium).ToList();
        Assert.Equal(premiums.OrderBy(p => p), premiums);
    }

    [Fact]
    public void RatedQuote_AttachesEveryOfferToItself()
    {
        var quote = TestQuotes.Clean();

        quote.MarkRated([TestQuotes.Offer(), TestQuotes.Offer("carrier-b")], Later);

        Assert.All(quote.Offers, offer => Assert.Equal(quote.Id, offer.QuoteId));
    }

    [Fact]
    public void MarkRated_WithNoOffers_IsRejected()
    {
        var quote = TestQuotes.Clean();

        var ex = Assert.Throws<DomainException>(() => quote.MarkRated([], Later));
        Assert.Contains("at least one carrier offer", ex.Message);
    }

    [Fact]
    public void RatedToRated_IsRejected()
    {
        // The duplicate-delivery guard: once Rated, the quote is frozen.
        var quote = TestQuotes.Clean();
        quote.MarkRated([TestQuotes.Offer()], Later);

        Assert.Throws<DomainException>(() => quote.MarkRated([TestQuotes.Offer()], Later));
    }

    [Fact]
    public void RatedToReferral_IsRejected()
    {
        var quote = TestQuotes.Clean();
        quote.MarkRated([TestQuotes.Offer()], Later);

        Assert.Throws<DomainException>(() => quote.MarkReferral("late referral", Later));
    }

    [Fact]
    public void RatedToDeclined_IsRejected()
    {
        var quote = TestQuotes.Clean();
        quote.MarkRated([TestQuotes.Offer()], Later);

        Assert.Throws<DomainException>(() => quote.MarkDecline("late decline", Later));
    }

    [Fact]
    public void PendingToReferral_IsAllowedAndClearsPremium()
    {
        var quote = TestQuotes.Clean();

        quote.MarkReferral("No prior insurance on record.", Later);

        Assert.Equal(QuoteStatus.Referral, quote.Status);
        Assert.Null(quote.AnnualPremium);
        Assert.Equal("No prior insurance on record.", quote.DecisionReason);
        Assert.True(quote.IsAlreadyUnderwritten());
    }

    [Fact]
    public void PendingToDeclined_IsAllowed()
    {
        var quote = TestQuotes.Clean();

        quote.MarkDecline("Vehicle too old.", Later);

        Assert.Equal(QuoteStatus.Declined, quote.Status);
        Assert.Equal("Vehicle too old.", quote.DecisionReason);
        Assert.True(quote.IsAlreadyUnderwritten());
    }

    [Fact]
    public void DeclinedToProcessing_IsRejected()
    {
        var quote = TestQuotes.Clean();
        quote.MarkDecline("Vehicle too old.", Later);

        Assert.Throws<DomainException>(() => quote.MarkProcessing(Later));
    }

    [Fact]
    public void ReferralToProcessing_IsRejected()
    {
        var quote = TestQuotes.Clean();
        quote.MarkReferral("Manual review.", Later);

        Assert.Throws<DomainException>(() => quote.MarkProcessing(Later));
    }

    [Theory]
    [InlineData(QuoteStatus.Rated, true)]
    [InlineData(QuoteStatus.Referral, true)]
    [InlineData(QuoteStatus.Declined, true)]
    [InlineData(QuoteStatus.FollowUp, true)]
    [InlineData(QuoteStatus.Pending, false)]
    [InlineData(QuoteStatus.Processing, false)]
    public void TerminalStatusesAreCorrectlyIdentified(QuoteStatus status, bool expectedTerminal)
    {
        // The React client stops polling on exactly these states.
        Assert.Equal(expectedTerminal, status.IsTerminal());
    }

    [Fact]
    public void SelectOfferForBinding_DefaultsToTheCheapestOffer()
    {
        var quote = TestQuotes.Clean();
        quote.MarkRated(
            [
                TestQuotes.Offer("carrier-a", CoverageTier.Standard, 1_500m),
                TestQuotes.Offer("carrier-b", CoverageTier.Standard, 1_050m)
            ],
            Later);

        var selected = quote.SelectOfferForBinding(offerId: null);

        Assert.Equal("carrier-b", selected.CarrierId);
        Assert.Equal(1_050m, selected.AnnualPremium);
    }

    [Fact]
    public void SelectOfferForBinding_HonoursAnExplicitChoice()
    {
        var quote = TestQuotes.Clean();
        quote.MarkRated(
            [
                TestQuotes.Offer("carrier-a", CoverageTier.Premium, 2_200m),
                TestQuotes.Offer("carrier-b", CoverageTier.Basic, 900m)
            ],
            Later);
        var premiumTierOffer = quote.Offers.Single(o => o.CoverageTier == CoverageTier.Premium);

        var selected = quote.SelectOfferForBinding(premiumTierOffer.Id);

        Assert.Equal(premiumTierOffer.Id, selected.Id);
    }

    [Fact]
    public void SelectOfferForBinding_RejectsAnOfferFromAnotherQuote()
    {
        var quote = TestQuotes.Clean();
        quote.MarkRated([TestQuotes.Offer()], Later);

        Assert.Throws<DomainException>(() => quote.SelectOfferForBinding(Guid.NewGuid()));
    }

    [Fact]
    public void SelectOfferForBinding_RejectsAnUnratedQuote()
    {
        var quote = TestQuotes.Clean();
        quote.MarkReferral("Manual review.", Later);

        var ex = Assert.Throws<DomainException>(() => quote.SelectOfferForBinding(null));
        Assert.Contains("Rated", ex.Message);
    }

    [Fact]
    public void Submit_NormalisesStateAndTrimsText()
    {
        var quote = Quote.Submit(
            "Q-2026-000099", "  Dana Reyes  ", "ga", 2024, " Honda ", " Accord ",
            9_000, 24, QuoteChannel.LegacyWebForms, "corr-1", TestQuotes.Now);

        Assert.Equal("Dana Reyes", quote.CustomerName);
        Assert.Equal("GA", quote.State);
        Assert.Equal("Honda", quote.VehicleMake);
        Assert.Equal("Accord", quote.VehicleModel);
    }

    [Fact]
    public void Submit_RecordsTheOriginatingChannel()
    {
        // Proves the legacy adapter and the modern API are distinguishable in data
        // while sharing the same code path.
        var legacy = TestQuotes.Clean(channel: QuoteChannel.LegacyWebForms);
        var modern = TestQuotes.Clean(channel: QuoteChannel.Modern);

        Assert.Equal(QuoteChannel.LegacyWebForms, legacy.Channel);
        Assert.Equal(QuoteChannel.Modern, modern.Channel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_RejectsBlankCustomerName(string name)
    {
        Assert.Throws<DomainException>(() => TestQuotes.Clean(customerName: name));
    }

    [Theory]
    [InlineData("G")]
    [InlineData("GEO")]
    [InlineData("")]
    public void Submit_RejectsInvalidStateCode(string state)
    {
        Assert.Throws<DomainException>(() => TestQuotes.Clean(state: state));
    }

    [Theory]
    [InlineData(1899)]
    [InlineData(2101)]
    public void Submit_RejectsOutOfRangeVehicleYear(int year)
    {
        Assert.Throws<DomainException>(() => TestQuotes.Clean(vehicleYear: year));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(200_001)]
    public void Submit_RejectsOutOfRangeMileage(int mileage)
    {
        Assert.Throws<DomainException>(() => TestQuotes.Clean(annualMileage: mileage));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(601)]
    public void Submit_RejectsOutOfRangePriorInsuranceMonths(int months)
    {
        Assert.Throws<DomainException>(() => TestQuotes.Clean(priorInsuranceMonths: months));
    }
}
