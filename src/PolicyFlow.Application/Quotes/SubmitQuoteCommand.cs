using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Messaging;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Quotes;

/// <summary>Request to open a new quote.</summary>
public sealed record SubmitQuoteCommand
{
    public required string CustomerName { get; init; }
    public required string State { get; init; }
    public required int VehicleYear { get; init; }
    public required string VehicleMake { get; init; }
    public required string VehicleModel { get; init; }
    public required int AnnualMileage { get; init; }
    public required int PriorInsuranceMonths { get; init; }

    /// <summary>Which client submitted this. Set by the controller, not the caller.</summary>
    public QuoteChannel Channel { get; init; } = QuoteChannel.Modern;

    /// <summary>
    /// The authenticated agent submitting the quote, or null when anonymous. Set by
    /// the controller from the bearer token, never from the request body.
    /// </summary>
    public string? SubmittedBy { get; init; }
}

/// <summary>What the API returns with its 202 Accepted.</summary>
public sealed record SubmitQuoteResult
{
    public required Guid QuoteId { get; init; }
    public required string QuoteNumber { get; init; }
    public required string Status { get; init; }
    public required string StatusUrl { get; init; }
    public required string CorrelationId { get; init; }
}

/// <summary>
/// Accepts a quote and hands underwriting off to the background worker.
/// </summary>
/// <remarks>
/// <para>
/// This handler deliberately does <b>not</b> price the quote. It performs exactly
/// one transaction — insert the Pending quote, insert the <c>QuoteSubmitted</c>
/// outbox row, commit — and returns 202. Rating requires calls to five carrier
/// gateways and a prior-insurance bureau; holding an HTTP request open for that
/// would couple the customer's page load to the slowest third party in the chain.
/// </para>
/// <para>
/// The outbox insert shares the quote's transaction on purpose. Publishing to
/// Service Bus here instead would open a window where the quote is committed but
/// the event was never sent, leaving a Pending quote nobody underwrites.
/// </para>
/// </remarks>
public sealed class SubmitQuoteHandler(
    IQuoteRepository quotes,
    IOutboxRepository outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    ICorrelationContext correlation,
    ILogger<SubmitQuoteHandler> logger)
{
    public async Task<SubmitQuoteResult> HandleAsync(SubmitQuoteCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var now = clock.UtcNow;
        var correlationId = correlation.CorrelationId;

        var result = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var quoteNumber = await quotes.NextQuoteNumberAsync(now.Year, token);

            var quote = Quote.Submit(
                quoteNumber: quoteNumber,
                customerName: command.CustomerName,
                state: command.State,
                vehicleYear: command.VehicleYear,
                vehicleMake: command.VehicleMake,
                vehicleModel: command.VehicleModel,
                annualMileage: command.AnnualMileage,
                priorInsuranceMonths: command.PriorInsuranceMonths,
                channel: command.Channel,
                correlationId: correlationId,
                nowUtc: now,
                submittedBy: command.SubmittedBy);

            await quotes.AddAsync(quote, token);

            // The message id is generated here and reused as the Service Bus
            // MessageId so the consumer can deduplicate redeliveries.
            var messageId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new QuoteSubmittedEvent
            {
                MessageId = messageId,
                QuoteId = quote.Id,
                QuoteNumber = quote.QuoteNumber,
                OccurredUtc = now,
                CorrelationId = correlationId
            });

            await outbox.AddAsync(
                OutboxMessage.Create(QuoteSubmittedEvent.EventType, payload, correlationId, now, messageId),
                token);

            // Single SaveChanges: quote row and outbox row commit together.
            await unitOfWork.SaveChangesAsync(token);

            return quote;
        }, ct);

        var elapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        logger.LogInformation(
            "Quote accepted. Operation={Operation} QuoteId={QuoteId} QuoteNumber={QuoteNumber} " +
            "Channel={Channel} State={State} CorrelationId={CorrelationId} ElapsedMs={ElapsedMs} Result={Result}",
            "SubmitQuote", result.Id, result.QuoteNumber, command.Channel, result.State,
            correlationId, elapsedMs, "Accepted");

        return new SubmitQuoteResult
        {
            QuoteId = result.Id,
            QuoteNumber = result.QuoteNumber,
            Status = result.Status.ToString(),
            StatusUrl = $"/api/quotes/{result.Id}",
            CorrelationId = correlationId
        };
    }
}
