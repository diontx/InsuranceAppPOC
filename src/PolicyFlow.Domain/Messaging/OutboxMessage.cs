namespace PolicyFlow.Domain.Messaging;

/// <summary>
/// A domain event staged for publication inside the same SQL transaction that
/// changed the business data.
/// </summary>
/// <remarks>
/// <para>
/// This is the transactional outbox pattern, and it closes a specific reliability
/// gap: if the API committed a quote and <i>then</i> published to Service Bus, a
/// crash between the two would leave a Pending quote nobody will ever underwrite.
/// Writing the event to this table in the same transaction makes "quote saved" and
/// "event will be sent" a single atomic fact.
/// </para>
/// <para>
/// A background publisher then moves rows to Service Bus and only stamps
/// <see cref="ProcessedUtc"/> <i>after</i> a successful send. Marking it before the
/// send would reintroduce the very gap the outbox removes, and at-least-once
/// delivery is why the consumer must be idempotent.
/// </para>
/// </remarks>
public class OutboxMessage
{
    private OutboxMessage() { }

    /// <summary>
    /// Also used as the Service Bus <c>MessageId</c>, which is what lets the
    /// consumer recognise a redelivery via <see cref="ProcessedMessage"/>.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>Logical event name, e.g. <c>QuoteSubmitted</c>.</summary>
    public string EventType { get; private set; } = null!;

    /// <summary>JSON event body. Contains identifiers only, never secrets.</summary>
    public string Payload { get; private set; } = null!;

    public DateTime OccurredUtc { get; private set; }

    /// <summary>Set only after the broker has acknowledged the send. Null means unsent.</summary>
    public DateTime? ProcessedUtc { get; private set; }

    /// <summary>Failed publish attempts so far.</summary>
    public int RetryCount { get; private set; }

    /// <summary>Last publish error, truncated. Retained for troubleshooting.</summary>
    public string? LastError { get; private set; }

    public string CorrelationId { get; private set; } = null!;

    public static OutboxMessage Create(
        string eventType,
        string payload,
        string correlationId,
        DateTime nowUtc,
        Guid? messageId = null) =>
        new()
        {
            Id = messageId ?? Guid.NewGuid(),
            EventType = eventType,
            Payload = payload,
            CorrelationId = correlationId,
            OccurredUtc = nowUtc,
            RetryCount = 0
        };

    /// <summary>Call only after the broker has acknowledged the send.</summary>
    public void MarkProcessed(DateTime nowUtc)
    {
        ProcessedUtc = nowUtc;
        LastError = null;
    }

    /// <summary>
    /// Records a failed publish attempt. The row stays unprocessed so the next
    /// publisher sweep retries it.
    /// </summary>
    public void MarkFailed(string error)
    {
        RetryCount++;
        LastError = error.Length > 1000 ? error[..1000] : error;
    }
}
