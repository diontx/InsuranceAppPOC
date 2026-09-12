namespace PolicyFlow.Domain.Messaging;

/// <summary>
/// The consumer-side deduplication ledger.
/// </summary>
/// <remarks>
/// <para>
/// Service Bus guarantees at-least-once delivery, so the underwriting worker must
/// assume any message can arrive twice: after a lock expiry, after a transient
/// failure, after a worker restart mid-handler. A row here is inserted in the same
/// transaction as the business update, and <c>(MessageId, Consumer)</c> is UNIQUE.
/// </para>
/// <para>
/// The composite key is intentional. Keying on <c>MessageId</c> alone would mean a
/// second, unrelated consumer of the same event could never process it — an easy
/// mistake that only surfaces when a second subscriber is added later.
/// </para>
/// </remarks>
public class ProcessedMessage
{
    private ProcessedMessage() { }

    public Guid MessageId { get; private set; }

    /// <summary>Logical consumer name, e.g. <c>UnderwritingWorker</c>.</summary>
    public string Consumer { get; private set; } = null!;

    public DateTime ProcessedUtc { get; private set; }

    public string CorrelationId { get; private set; } = null!;

    public static ProcessedMessage Create(Guid messageId, string consumer, string correlationId, DateTime nowUtc) =>
        new()
        {
            MessageId = messageId,
            Consumer = consumer,
            CorrelationId = correlationId,
            ProcessedUtc = nowUtc
        };
}
