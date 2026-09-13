using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Tests;

/// <summary>Which agent a quote belongs to.</summary>
public class QuoteAttributionTests
{
    private static Quote SubmitAs(string? submittedBy) =>
        Quote.Submit(
            "Q-2026-000500", "Dana Reyes", "GA", 2023, "Honda", "Accord",
            9_000, 24, QuoteChannel.Modern, "corr-1", TestQuotes.Now, submittedBy);

    [Fact]
    public void Submit_RecordsTheSubmittingAgent_Trimmed()
    {
        Assert.Equal("a.patel", SubmitAs("  a.patel  ").SubmittedBy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_WithoutAnAgent_IsUnattributed(string? submittedBy)
    {
        // A self-service customer or the legacy service principal: in no agent's list.
        Assert.Null(SubmitAs(submittedBy).SubmittedBy);
    }

    [Fact]
    public void Submit_RejectsAnAgentIdentityLongerThanTheSchemaStores()
    {
        Assert.Throws<DomainException>(() => SubmitAs(new string('a', Quote.MaxSubmitterLength + 1)));
    }

    [Fact]
    public void ExistingCallers_RemainUnattributed()
    {
        // The parameter is optional so the automated and legacy paths are unchanged.
        Assert.Null(TestQuotes.Clean().SubmittedBy);
    }
}
