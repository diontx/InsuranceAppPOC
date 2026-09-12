using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Quotes;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Messaging;
using PolicyFlow.Infrastructure.Messaging;
using PolicyFlow.Infrastructure.Persistence;

namespace PolicyFlow.Worker;

/// <summary>
/// Consumes <c>QuoteSubmitted</c> from Service Bus and runs underwriting.
/// </summary>
/// <remarks>
/// <para>
/// The consumer half of the asynchronous pipeline. Service Bus guarantees
/// <b>at-least-once</b> delivery, so this handler is built on the assumption that
/// any message can arrive more than once — after a lock expiry, a transient
/// failure, or a worker restart part-way through a handler.
/// </para>
/// <para>Three independent layers make redelivery safe:</para>
/// <list type="number">
///   <item><b>The <c>ProcessedMessage</c> ledger.</b> A primary-key seek on
///         <c>(MessageId, Consumer)</c> before any work. The cheap common case.</item>
///   <item><b>The quote state machine.</b> <c>UnderwriteQuoteHandler</c> refuses to
///         re-rate a quote that is already terminal, so even if the ledger read
///         misses, the quote cannot be priced twice.</item>
///   <item><b>The composite primary key.</b> If two workers race, the second
///         insert violates it and that work is discarded.</item>
/// </list>
/// <para>
/// Error handling distinguishes two kinds of failure, because they need opposite
/// responses. A <i>transient</i> failure (the verification dependency is down) is
/// abandoned so Service Bus redelivers it and it eventually succeeds. A
/// <i>permanent</i> failure (the quote violates a domain rule, or its row is gone)
/// is dead-lettered immediately: retrying it will fail identically forever, and
/// leaving it in the queue blocks throughput while filling the logs.
/// </para>
/// </remarks>
public sealed class UnderwritingWorker(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    IOptions<ServiceBusOptions> options,
    ILogger<UnderwritingWorker> logger) : BackgroundService
{
    /// <summary>
    /// Consumer name recorded in <c>ProcessedMessage</c>. A constant, because
    /// changing it would make every previously processed message look unprocessed.
    /// </summary>
    public const string ConsumerName = "UnderwritingWorker";

    private ServiceBusProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;

        if (config.IsDisabled)
        {
            logger.LogCritical(
                "Worker cannot start: no Service Bus connection string is configured. " +
                "Set SERVICEBUS_CONNECTION_STRING. Operation={Operation} Result={Result}",
                "WorkerStartup", "NotConfigured");
            return;
        }

        _processor = client.CreateProcessor(config.QueueName, new ServiceBusProcessorOptions
        {
            // Manual completion. With autoComplete the broker would consider the
            // message handled the moment the handler returns — including when it
            // threw — so a failed underwriting run would be silently lost.
            AutoCompleteMessages = false,

            MaxConcurrentCalls = config.MaxConcurrentCalls,

            // Underwriting takes roughly 1-2 seconds (verification plus a five-way
            // carrier fan-out). Five minutes of lock renewal is generous headroom,
            // so a slow carrier does not cause a lock expiry and a redelivery of
            // work that is still in progress.
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),

            PrefetchCount = config.MaxConcurrentCalls * 2
        });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync += HandleProcessorErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken);

        logger.LogInformation(
            "Underwriting worker started. Operation={Operation} Queue={Queue} " +
            "MaxConcurrentCalls={MaxConcurrentCalls} Consumer={Consumer}",
            "WorkerStartup", config.QueueName, config.MaxConcurrentCalls, ConsumerName);

        // Park until shutdown. The processor runs the handler on its own threads.
        var shutdown = new TaskCompletionSource();
        stoppingToken.Register(() => shutdown.TrySetResult());
        await shutdown.Task;

        logger.LogInformation("Underwriting worker stopping. Operation={Operation}", "WorkerShutdown");
        await _processor.StopProcessingAsync(CancellationToken.None);
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        var correlationId = message.CorrelationId ?? "unknown";

        // The outbox row id is reused as the broker MessageId, so this is the
        // deduplication key.
        if (!Guid.TryParse(message.MessageId, out var messageId))
        {
            logger.LogError(
                "Dead-lettering message with an unparseable MessageId. Operation={Operation} " +
                "RawMessageId={RawMessageId} CorrelationId={CorrelationId} Result={Result}",
                "HandleMessage", message.MessageId, correlationId, "DeadLettered");

            // Permanent: a malformed id will never become valid. Retrying is pointless.
            await args.DeadLetterMessageAsync(
                message, "InvalidMessageId", "MessageId is not a GUID.", args.CancellationToken);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["MessageId"] = messageId,
            ["Consumer"] = ConsumerName
        });

        QuoteSubmittedEvent? submitted;

        try
        {
            submitted = JsonSerializer.Deserialize<QuoteSubmittedEvent>(
                message.Body.ToString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Dead-lettering message with an unreadable body. Operation={Operation} Result={Result}",
                "HandleMessage", "DeadLettered");

            // Permanent: malformed JSON does not repair itself.
            await args.DeadLetterMessageAsync(
                message, "DeserializationFailed", "Body is not a valid QuoteSubmitted event.",
                args.CancellationToken);
            return;
        }

        if (submitted is null || submitted.QuoteId == Guid.Empty)
        {
            logger.LogError(
                "Dead-lettering message with no quote id. Operation={Operation} Result={Result}",
                "HandleMessage", "DeadLettered");

            await args.DeadLetterMessageAsync(
                message, "MissingQuoteId", "Event does not carry a quote id.", args.CancellationToken);
            return;
        }

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        // A scope per message: the DbContext is scoped, and the processor invokes
        // this handler concurrently. Sharing one context across concurrent messages
        // would corrupt change tracking.
        using var serviceScope = scopeFactory.CreateScope();
        var services = serviceScope.ServiceProvider;

        var processedMessages = services.GetRequiredService<IProcessedMessageRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var underwrite = services.GetRequiredService<UnderwriteQuoteHandler>();
        var clock = services.GetRequiredService<IClock>();

        try
        {
            // --- Layer 1: the deduplication ledger.
            if (await processedMessages.HasBeenProcessedAsync(
                    messageId, ConsumerName, args.CancellationToken))
            {
                logger.LogInformation(
                    "Skipping message already processed by this consumer. Operation={Operation} " +
                    "QuoteId={QuoteId} Result={Result}",
                    "HandleMessage", submitted.QuoteId, "DuplicateSkipped");

                // Complete it: the work is done, and leaving it would redeliver forever.
                await args.CompleteMessageAsync(message, args.CancellationToken);
                return;
            }

            // --- Underwrite. Layer 2 lives inside this handler: it refuses to
            //     re-rate a quote that has already reached a terminal state.
            var result = await underwrite.HandleAsync(submitted.QuoteId, args.CancellationToken);

            if (result.Kind == UnderwritingResultKind.NotFound)
            {
                logger.LogError(
                    "Dead-lettering message for a quote that does not exist. Operation={Operation} " +
                    "QuoteId={QuoteId} Result={Result}",
                    "HandleMessage", submitted.QuoteId, "DeadLettered");

                // Permanent: the row is not coming back. Retrying would loop until
                // the delivery count is exhausted, delaying real work.
                await args.DeadLetterMessageAsync(
                    message, "QuoteNotFound", $"Quote {submitted.QuoteId} does not exist.",
                    args.CancellationToken);
                return;
            }

            // --- Layer 3: record the message as processed.
            await processedMessages.AddAsync(
                ProcessedMessage.Create(messageId, ConsumerName, correlationId, clock.UtcNow),
                args.CancellationToken);

            await unitOfWork.SaveChangesAsync(args.CancellationToken);

            var elapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            logger.LogInformation(
                "Message processed. Operation={Operation} QuoteId={QuoteId} QuoteNumber={QuoteNumber} " +
                "Outcome={Outcome} Status={Status} OfferCount={OfferCount} BestPremium={BestPremium} " +
                "DeliveryCount={DeliveryCount} ElapsedMs={ElapsedMs} Result={Result}",
                "HandleMessage", result.QuoteId, result.QuoteNumber, result.Kind, result.Status,
                result.OfferCount, result.BestAnnualPremium, message.DeliveryCount, elapsedMs, "Completed");

            await args.CompleteMessageAsync(message, args.CancellationToken);
        }
        catch (DuplicateProcessedMessageException)
        {
            // Two workers processed the same message concurrently and this one lost
            // the race to record it. The other's work stands; discard ours.
            logger.LogWarning(
                "Concurrent consumer already recorded this message. Operation={Operation} " +
                "QuoteId={QuoteId} Result={Result}",
                "HandleMessage", submitted.QuoteId, "RaceLost");

            await args.CompleteMessageAsync(message, args.CancellationToken);
        }
        catch (DependencyUnavailableException ex)
        {
            // TRANSIENT. Abandon so the broker redelivers: the dependency is
            // expected to recover. The lock is released immediately rather than
            // waiting for it to expire.
            logger.LogWarning(
                ex,
                "Transient dependency failure; abandoning for redelivery. Operation={Operation} " +
                "QuoteId={QuoteId} DeliveryCount={DeliveryCount} MaxDeliveryCount={MaxDeliveryCount} Result={Result}",
                "HandleMessage", submitted.QuoteId, message.DeliveryCount,
                options.Value.MaxDeliveryCount, "Abandoned");

            await args.AbandonMessageAsync(message, cancellationToken: args.CancellationToken);
        }
        catch (DomainException ex)
        {
            // PERMANENT. The quote violates a business rule, so every retry
            // produces the same failure. Dead-letter it for a human to inspect.
            logger.LogError(
                ex,
                "Domain rule rejected this quote; dead-lettering. Operation={Operation} " +
                "QuoteId={QuoteId} Reason={Reason} Result={Result}",
                "HandleMessage", submitted.QuoteId, ex.Message, "DeadLettered");

            await args.DeadLetterMessageAsync(
                message, "DomainRuleViolation", Truncate(ex.Message, 4096), args.CancellationToken);
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-message. Abandon so it is picked up after restart
            // instead of being consumed and lost.
            logger.LogInformation(
                "Worker shutting down mid-message; abandoning. Operation={Operation} " +
                "QuoteId={QuoteId} Result={Result}",
                "HandleMessage", submitted.QuoteId, "AbandonedOnShutdown");

            await args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Unknown. Abandon rather than dead-letter or complete: it may be
            // transient, and Service Bus dead-letters it automatically once
            // MaxDeliveryCount is exhausted. Never swallow it.
            logger.LogError(
                ex,
                "Unhandled error processing message; abandoning for retry. Operation={Operation} " +
                "QuoteId={QuoteId} DeliveryCount={DeliveryCount} Result={Result}",
                "HandleMessage", submitted.QuoteId, message.DeliveryCount, "Abandoned");

            await args.AbandonMessageAsync(message, cancellationToken: args.CancellationToken);
        }
    }

    /// <summary>
    /// Handles processor-level errors: connection loss, lock renewal failure,
    /// credential problems. These are not tied to one message.
    /// </summary>
    private Task HandleProcessorErrorAsync(ProcessErrorEventArgs args)
    {
        // Logged, not rethrown. The processor recovers on its own; throwing here
        // would take down the worker over a transient network blip.
        logger.LogError(
            args.Exception,
            "Service Bus processor error. Operation={Operation} ErrorSource={ErrorSource} " +
            "EntityPath={EntityPath} Result={Result}",
            "ServiceBusProcessor", args.ErrorSource, args.EntityPath, "ProcessorError");

        return Task.CompletedTask;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
