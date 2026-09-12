namespace PolicyFlow.Domain.Messaging;

/// <summary>
/// Integration event published when a quote is accepted for underwriting.
/// </summary>
/// <remarks>
/// Deliberately thin. It carries identifiers, not a snapshot of the quote, so the
/// worker reads current state from SQL rather than acting on data that may be
/// stale by the time the message is delivered. That also keeps risk details off
/// the message bus.
/// </remarks>
public sealed record QuoteSubmittedEvent
{
    public const string EventType = "QuoteSubmitted";

    /// <summary>Matches <see cref="OutboxMessage.Id"/> and the Service Bus MessageId.</summary>
    public required Guid MessageId { get; init; }

    public required Guid QuoteId { get; init; }

    public required string QuoteNumber { get; init; }

    public required DateTime OccurredUtc { get; init; }

    public required string CorrelationId { get; init; }
}
