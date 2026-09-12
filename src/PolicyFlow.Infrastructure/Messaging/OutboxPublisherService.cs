using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Common;

namespace PolicyFlow.Infrastructure.Messaging;

/// <summary>
/// Background service that moves staged outbox rows onto Service Bus.
/// </summary>
/// <remarks>
/// <para>
/// This is the second half of the transactional outbox. The API committed the quote
/// and the event row together; this loop makes the event actually leave the
/// process.
/// </para>
/// <para>The ordering rule is the entire point and is easy to get wrong:</para>
/// <list type="number">
///   <item>Read unsent rows.</item>
///   <item>Send to the broker and <b>wait for the acknowledgement</b>.</item>
///   <item>Only then stamp <c>ProcessedUtc</c> and commit.</item>
/// </list>
/// <para>
/// Marking the row processed before the send would reintroduce exactly the gap the
/// outbox exists to close: a crash in between loses the event with no trace. Doing
/// it in this order means a crash instead causes a <i>re-send</i> — the message is
/// delivered twice, which is safe, because the consumer deduplicates on
/// <c>ProcessedMessage</c>.
/// </para>
/// <para>
/// This is the source of at-least-once (never exactly-once) delivery in the
/// system. Exactly-once across a database and a broker is not achievable without
/// distributed transactions; the practical answer is at-least-once delivery plus
/// an idempotent consumer.
/// </para>
/// </remarks>
public sealed class OutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    IOptions<ServiceBusOptions> options,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    /// <summary>Consecutive failures after which the loop backs off.</summary>
    private const int BackoffAfterConsecutiveFailures = 3;

    private static readonly TimeSpan BackoffDelay = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;

        if (config.IsDisabled)
        {
            logger.LogWarning(
                "Outbox publisher is not running: no Service Bus connection string is configured. " +
                "Quotes will be accepted and persisted but will remain Pending. Operation={Operation} Result={Result}",
                "OutboxPublisher", "Disabled");
            return;
        }

        logger.LogInformation(
            "Outbox publisher started. Operation={Operation} Queue={Queue} BatchSize={BatchSize} PollSeconds={PollSeconds}",
            "OutboxPublisher", config.QueueName, config.OutboxBatchSize, config.OutboxPollSeconds);

        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, config.OutboxPollSeconds));
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(config.OutboxBatchSize, stoppingToken);
                consecutiveFailures = 0;

                // When a full batch went out there is probably more waiting, so
                // sweep again immediately instead of idling for the poll interval.
                if (published >= config.OutboxBatchSize)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;

                // Log and keep going. Unsent rows stay unsent and will be retried;
                // the loop must not die, or every later quote stalls in Pending.
                logger.LogError(
                    ex,
                    "Outbox sweep failed. Operation={Operation} ConsecutiveFailures={ConsecutiveFailures} Result={Result}",
                    "OutboxPublisher", consecutiveFailures, "SweepFailed");

                if (consecutiveFailures >= BackoffAfterConsecutiveFailures)
                {
                    // Something systemic is wrong (broker down, credentials rotated).
                    // Back off so the logs stay readable and the broker is not hammered.
                    logger.LogWarning(
                        "Backing off outbox publishing for {BackoffSeconds}s after repeated failures. Operation={Operation}",
                        BackoffDelay.TotalSeconds, "OutboxPublisher");

                    await Task.Delay(BackoffDelay, stoppingToken);
                    consecutiveFailures = 0;
                    continue;
                }
            }

            await Task.Delay(pollInterval, stoppingToken);
        }

        logger.LogInformation("Outbox publisher stopped. Operation={Operation}", "OutboxPublisher");
    }

    /// <summary>Publishes one batch. Returns the number successfully sent.</summary>
    private async Task<int> PublishBatchAsync(int batchSize, CancellationToken ct)
    {
        // A fresh scope per sweep: the DbContext is scoped, and a BackgroundService
        // is a singleton. Resolving a scoped service directly into a singleton is
        // the classic captive-dependency bug.
        //
        // CreateAsyncScope, not CreateScope: a synchronous scope throws when it
        // contains a service that implements only IAsyncDisposable, and an async
        // scope also awaits disposal properly rather than blocking.
        await using var scope = scopeFactory.CreateAsyncScope();

        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var pending = await outbox.GetUnprocessedAsync(batchSize, ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var sent = 0;

        foreach (var message in pending)
        {
            try
            {
                // Send first, and await the broker acknowledgement.
                await publisher.PublishAsync(
                    message.Id, message.EventType, message.Payload, message.CorrelationId, ct);

                // Only now is it safe to mark the row processed.
                message.MarkProcessed(clock.UtcNow);
                sent++;
            }
            catch (Exception ex)
            {
                // Record the attempt but leave ProcessedUtc null so the next sweep
                // retries this row. Per-message isolation: one bad message must not
                // block the rest of the batch.
                message.MarkFailed(ex.Message);

                logger.LogError(
                    ex,
                    "Outbox message publish failed and will be retried. Operation={Operation} " +
                    "MessageId={MessageId} EventType={EventType} RetryCount={RetryCount} " +
                    "CorrelationId={CorrelationId} Result={Result}",
                    "OutboxPublisher", message.Id, message.EventType, message.RetryCount,
                    message.CorrelationId, "PublishFailed");
            }
        }

        // One SaveChanges for the whole batch: processed stamps and retry counters
        // commit together.
        await unitOfWork.SaveChangesAsync(ct);

        if (sent > 0)
        {
            logger.LogInformation(
                "Outbox batch published. Operation={Operation} Sent={Sent} Attempted={Attempted} Result={Result}",
                "OutboxPublisher", sent, pending.Count, "BatchComplete");
        }

        return sent;
    }
}
