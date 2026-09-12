using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PolicyFlow.Application.Abstractions;

namespace PolicyFlow.Infrastructure.Messaging;

/// <summary>Service Bus connection settings, bound from environment variables.</summary>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Connection string. Supplied through <c>SERVICEBUS_CONNECTION_STRING</c> and
    /// never committed. For the local emulator it carries
    /// <c>UseDevelopmentEmulator=true</c>.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string QueueName { get; set; } = "quote-submitted";

    /// <summary>Outbox rows published per sweep.</summary>
    public int OutboxBatchSize { get; set; } = 20;

    /// <summary>Seconds between outbox sweeps.</summary>
    public int OutboxPollSeconds { get; set; } = 2;

    /// <summary>Concurrent message handlers in the worker.</summary>
    public int MaxConcurrentCalls { get; set; } = 4;

    /// <summary>
    /// Deliveries after which Service Bus dead-letters a message. Configured on the
    /// queue itself (see <c>infra/servicebus/config.json</c>); mirrored here so the
    /// worker can log how close a retrying message is to being dead-lettered.
    /// </summary>
    public int MaxDeliveryCount { get; set; } = 5;

    /// <summary>
    /// True when messaging is not configured. The API then runs without the queue
    /// so the relational and REST layers can still be demonstrated.
    /// </summary>
    public bool IsDisabled => string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>
/// Publishes outbox messages to an Azure Service Bus queue.
/// </summary>
/// <remarks>
/// <para>
/// The correlation id travels in both <see cref="ServiceBusMessage.CorrelationId"/>
/// and an application property, so it survives into the worker's logs and a single
/// id traces a quote across API, SQL, queue and worker.
/// </para>
/// <para>
/// Failures are rethrown, never swallowed. The outbox publisher depends on the
/// exception to leave the row unprocessed; catching it here and logging would mark
/// the message sent when it was not, losing the event permanently.
/// </para>
/// </remarks>
public sealed class ServiceBusEventPublisher(
    ServiceBusClient client,
    IOptions<ServiceBusOptions> options,
    ILogger<ServiceBusEventPublisher> logger) : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender(options.Value.QueueName);

    public async Task PublishAsync(
        Guid messageId,
        string eventType,
        string payload,
        string correlationId,
        CancellationToken ct = default)
    {
        var message = new ServiceBusMessage(payload)
        {
            // The outbox row id doubles as the broker MessageId, which is what lets
            // the consumer recognise a redelivery via the ProcessedMessage ledger.
            MessageId = messageId.ToString(),
            CorrelationId = correlationId,
            Subject = eventType,
            ContentType = "application/json"
        };

        message.ApplicationProperties["EventType"] = eventType;
        message.ApplicationProperties["CorrelationId"] = correlationId;

        await _sender.SendMessageAsync(message, ct);

        logger.LogInformation(
            "Event published. Operation={Operation} MessageId={MessageId} EventType={EventType} " +
            "Queue={Queue} CorrelationId={CorrelationId} Result={Result}",
            "PublishEvent", messageId, eventType, options.Value.QueueName, correlationId, "Sent");
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync();
}
