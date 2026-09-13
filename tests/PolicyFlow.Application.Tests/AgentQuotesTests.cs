using PolicyFlow.Application.Quotes;

namespace PolicyFlow.Application.Tests;

/// <summary>An agent's own quotes: attribution on submit, and the list that reads it back.</summary>
public class AgentQuotesTests
{
    private static SubmitQuoteCommand Command(
        string customerName,
        string? submittedBy,
        int priorInsuranceMonths = 36) => new()
        {
            CustomerName = customerName,
            State = "GA",
            VehicleYear = 2023,
            VehicleMake = "Toyota",
            VehicleModel = "Camry",
            AnnualMileage = 12_000,
            PriorInsuranceMonths = priorInsuranceMonths,
            SubmittedBy = submittedBy
        };

    [Fact]
    public async Task SubmittedQuote_RecordsTheSubmittingAgent()
    {
        await using var harness = new TestHarness();

        var result = await harness.SubmitQuote.HandleAsync(Command("Dana Reyes", "a.patel"));

        Assert.Equal("a.patel", (await harness.Quotes.GetByIdAsync(result.QuoteId))!.SubmittedBy);
    }

    [Fact]
    public async Task AnonymousSubmission_IsNotAttributed()
    {
        await using var harness = new TestHarness();

        var result = await harness.SubmitQuote.HandleAsync(Command("Dana Reyes", submittedBy: null));

        Assert.Null((await harness.Quotes.GetByIdAsync(result.QuoteId))!.SubmittedBy);
    }

    [Fact]
    public async Task List_ReturnsOnlyThatAgentsQuotes_NewestFirst()
    {
        await using var harness = new TestHarness();

        var older = await harness.SubmitQuote.HandleAsync(Command("First Client", "a.patel"));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.SubmitQuote.HandleAsync(Command("Someone Else", "m.okafor"));
        await harness.SubmitQuote.HandleAsync(Command("Walk In", submittedBy: null));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var newer = await harness.SubmitQuote.HandleAsync(Command("Second Client", "a.patel"));

        var mine = await harness.GetQuote.ListSubmittedByAsync("a.patel");

        Assert.Equal([newer.QuoteId, older.QuoteId], mine.Select(q => q.QuoteId));
    }

    [Fact]
    public async Task List_ShowsEachQuotesCurrentUnderwritingOutcome()
    {
        await using var harness = new TestHarness();

        var rated = await harness.SubmitQuote.HandleAsync(Command("Clean Risk", "a.patel"));
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var referred = await harness.SubmitQuote.HandleAsync(Command("No History", "a.patel", priorInsuranceMonths: 0));
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var pending = await harness.SubmitQuote.HandleAsync(Command("Just Submitted", "a.patel"));

        await harness.Underwrite.HandleAsync(rated.QuoteId);
        await harness.Underwrite.HandleAsync(referred.QuoteId);
        harness.Db.ChangeTracker.Clear();

        var mine = (await harness.GetQuote.ListSubmittedByAsync("a.patel")).ToDictionary(q => q.QuoteId);

        Assert.Equal("Rated", mine[rated.QuoteId].Status);
        Assert.Equal(15, mine[rated.QuoteId].OfferCount);
        Assert.NotNull(mine[rated.QuoteId].AnnualPremium);

        Assert.Equal("Referral", mine[referred.QuoteId].Status);
        Assert.False(string.IsNullOrWhiteSpace(mine[referred.QuoteId].DecisionReason));

        Assert.Equal("Pending", mine[pending.QuoteId].Status);
        Assert.False(mine[pending.QuoteId].IsTerminal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task List_ForABlankAgent_IsEmpty(string agent)
    {
        // Anonymous submissions have a null SubmittedBy; a blank identity must never
        // be able to list them.
        await using var harness = new TestHarness();
        await harness.SubmitQuote.HandleAsync(Command("Walk In", submittedBy: null));

        Assert.Empty(await harness.GetQuote.ListSubmittedByAsync(agent));
    }
}
