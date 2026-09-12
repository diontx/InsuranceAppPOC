using Microsoft.EntityFrameworkCore;
using PolicyFlow.Application.Quotes;
using PolicyFlow.Domain.Messaging;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Tests;

/// <summary>
/// Quote submission. The behaviour under test is that the handler persists a
/// Pending quote and an outbox event together, and prices nothing.
/// </summary>
public class SubmitQuoteHandlerTests
{
    private static SubmitQuoteCommand ValidCommand(
        string customerName = "Alex Johnson",
        string state = "GA",
        int priorInsuranceMonths = 36) => new()
        {
            CustomerName = customerName,
            State = state,
            VehicleYear = 2023,
            VehicleMake = "Toyota",
            VehicleModel = "Camry",
            AnnualMileage = 12_000,
            PriorInsuranceMonths = priorInsuranceMonths
        };

    [Fact]
    public async Task SubmitQuote_CreatesAPendingQuote()
    {
        await using var harness = new TestHarness();

        var result = await harness.SubmitQuote.HandleAsync(ValidCommand());

        Assert.Equal("Pending", result.Status);
        Assert.NotEqual(Guid.Empty, result.QuoteId);

        var persisted = await harness.Quotes.GetByIdAsync(result.QuoteId);
        Assert.NotNull(persisted);
        Assert.Equal(QuoteStatus.Pending, persisted!.Status);
    }

    [Fact]
    public async Task SubmitQuote_DoesNotPriceTheQuote()
    {
        // The whole point of the 202: no premium exists yet, and no carrier has
        // been contacted. If this ever returned a price, underwriting would have
        // moved back into the request path.
        await using var harness = new TestHarness();

        var result = await harness.SubmitQuote.HandleAsync(ValidCommand());
        var persisted = await harness.Quotes.GetWithOffersAsync(result.QuoteId);

        Assert.Null(persisted!.AnnualPremium);
        Assert.Null(persisted.BestCarrierId);
        Assert.Empty(persisted.Offers);
    }

    [Fact]
    public async Task SubmitQuote_WritesTheOutboxEventInTheSameSave()
    {
        // The transactional-outbox invariant: the quote row and the event row must
        // both exist, or neither. A quote with no event would sit Pending forever.
        await using var harness = new TestHarness();

        var result = await harness.SubmitQuote.HandleAsync(ValidCommand());

        var outboxRows = await harness.Db.OutboxMessages.ToListAsync();
        var message = Assert.Single(outboxRows);

        Assert.Equal(QuoteSubmittedEvent.EventType, message.EventType);
        Assert.Null(message.ProcessedUtc);
        Assert.Equal(0, message.RetryCount);
        Assert.Contains(result.QuoteId.ToString(), message.Payload);
    }

    [Fact]
    public async Task OutboxMessageId_MatchesThePayloadMessageId()
    {
        // The outbox row id is reused as the Service Bus MessageId, which is what
        // lets the consumer deduplicate a redelivery. If these ever diverged,
        // deduplication would silently stop working.
        await using var harness = new TestHarness();

        await harness.SubmitQuote.HandleAsync(ValidCommand());

        var message = await harness.Db.OutboxMessages.SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<QuoteSubmittedEvent>(
            message.Payload,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(message.Id, payload!.MessageId);
    }

    [Fact]
    public async Task SubmitQuote_AssignsSequentialQuoteNumbers()
    {
        await using var harness = new TestHarness();

        var first = await harness.SubmitQuote.HandleAsync(ValidCommand());
        var second = await harness.SubmitQuote.HandleAsync(ValidCommand());
        var third = await harness.SubmitQuote.HandleAsync(ValidCommand());

        Assert.Equal("Q-2026-000001", first.QuoteNumber);
        Assert.Equal("Q-2026-000002", second.QuoteNumber);
        Assert.Equal("Q-2026-000003", third.QuoteNumber);
    }

    [Fact]
    public async Task SubmitQuote_PropagatesTheCorrelationIdToQuoteAndEvent()
    {
        // One id must reach the quote row and the message, or the end-to-end trace
        // breaks at the queue boundary.
        await using var harness = new TestHarness();
        harness.Correlation.CorrelationId = "trace-me-12345";

        var result = await harness.SubmitQuote.HandleAsync(ValidCommand());

        var quote = await harness.Quotes.GetByIdAsync(result.QuoteId);
        var message = await harness.Db.OutboxMessages.SingleAsync();

        Assert.Equal("trace-me-12345", result.CorrelationId);
        Assert.Equal("trace-me-12345", quote!.CorrelationId);
        Assert.Equal("trace-me-12345", message.CorrelationId);
        Assert.Contains("trace-me-12345", message.Payload);
    }

    [Fact]
    public async Task SubmitQuote_ReturnsAStatusUrlForPolling()
    {
        await using var harness = new TestHarness();

        var result = await harness.SubmitQuote.HandleAsync(ValidCommand());

        Assert.Equal($"/api/quotes/{result.QuoteId}", result.StatusUrl);
    }

    [Fact]
    public async Task SubmitQuote_RecordsTheModernChannelByDefault()
    {
        await using var harness = new TestHarness();

        var result = await harness.SubmitQuote.HandleAsync(ValidCommand());
        var quote = await harness.Quotes.GetByIdAsync(result.QuoteId);

        Assert.Equal(QuoteChannel.Modern, quote!.Channel);
    }

    [Fact]
    public async Task SubmitQuote_RejectsNullCommand()
    {
        await using var harness = new TestHarness();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.SubmitQuote.HandleAsync(null!));
    }

    [Fact]
    public async Task GetQuote_ReturnsNullForAnUnknownId()
    {
        await using var harness = new TestHarness();

        Assert.Null(await harness.GetQuote.HandleAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetQuote_ReportsPendingAsNonTerminal()
    {
        // The client stops polling on isTerminal, so this flag is load-bearing.
        await using var harness = new TestHarness();
        var result = await harness.SubmitQuote.HandleAsync(ValidCommand());

        var view = await harness.GetQuote.HandleAsync(result.QuoteId);

        Assert.False(view!.IsTerminal);
        Assert.Equal("Pending", view.Status);
        Assert.Null(view.OffersUrl);
    }

    [Fact]
    public async Task SelfServiceLookup_MatchesOnQuoteNumberAndSurname()
    {
        await using var harness = new TestHarness();
        var result = await harness.SubmitQuote.HandleAsync(ValidCommand(customerName: "Alex Johnson"));

        var found = await harness.GetQuote.LookupAsync(result.QuoteNumber, "Johnson");

        Assert.NotNull(found);
        Assert.Equal(result.QuoteId, found!.QuoteId);
    }

    [Fact]
    public async Task SelfServiceLookup_IsCaseInsensitiveOnTheSurname()
    {
        await using var harness = new TestHarness();
        var result = await harness.SubmitQuote.HandleAsync(ValidCommand(customerName: "Alex Johnson"));

        Assert.NotNull(await harness.GetQuote.LookupAsync(result.QuoteNumber, "johnson"));
        Assert.NotNull(await harness.GetQuote.LookupAsync(result.QuoteNumber, "JOHNSON"));
    }

    [Fact]
    public async Task SelfServiceLookup_RejectsAWrongSurname()
    {
        // The surname is the second factor: quote numbers are sequential and
        // therefore guessable, so a wrong name must not return the quote.
        await using var harness = new TestHarness();
        var result = await harness.SubmitQuote.HandleAsync(ValidCommand(customerName: "Alex Johnson"));

        Assert.Null(await harness.GetQuote.LookupAsync(result.QuoteNumber, "Smith"));
    }

    [Fact]
    public async Task SelfServiceLookup_RejectsAnUnknownQuoteNumber()
    {
        await using var harness = new TestHarness();

        Assert.Null(await harness.GetQuote.LookupAsync("Q-2026-999999", "Johnson"));
    }

    [Theory]
    [InlineData("", "Johnson")]
    [InlineData("Q-2026-000001", "")]
    [InlineData("   ", "   ")]
    public async Task SelfServiceLookup_RejectsBlankInput(string quoteNumber, string lastName)
    {
        await using var harness = new TestHarness();

        Assert.Null(await harness.GetQuote.LookupAsync(quoteNumber, lastName));
    }
}
