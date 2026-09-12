using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;

namespace PolicyFlow.Infrastructure.Messaging;

/// <summary>
/// Fallback publisher used when no Service Bus connection string is configured.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> a fake that pretends to succeed. It throws, so the outbox row
/// stays unprocessed and the event is not silently lost. A publisher that quietly
/// returned success would mark messages sent that never left the process — the
/// exact failure the outbox pattern exists to prevent.
/// </para>
/// <para>
/// Its purpose is to make misconfiguration diagnosable: the API still starts, still
/// accepts and persists quotes, and the logs say plainly why nothing is being
/// underwritten.
/// </para>
/// </remarks>
public sealed class NoOpEventPublisher(ILogger<NoOpEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(
        Guid messageId,
        string eventType,
        string payload,
        string correlationId,
        CancellationToken ct = default)
    {
        logger.LogError(
            "Cannot publish {EventType}: messaging is not configured. Set SERVICEBUS_CONNECTION_STRING. " +
            "Operation={Operation} MessageId={MessageId} CorrelationId={CorrelationId} Result={Result}",
            eventType, "PublishEvent", messageId, correlationId, "NotConfigured");

        throw new InvalidOperationException(
            "Service Bus is not configured, so events cannot be published. " +
            "Set SERVICEBUS_CONNECTION_STRING (see .env.example). The outbox row is left " +
            "unprocessed and will be published once messaging is available.");
    }
}
