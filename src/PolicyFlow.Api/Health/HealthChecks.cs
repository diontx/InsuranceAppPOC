using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PolicyFlow.Infrastructure.Messaging;
using PolicyFlow.Infrastructure.NoSql.Persistence;

namespace PolicyFlow.Api.Health;

/// <summary>
/// Readiness check for the MongoDB document store.
/// </summary>
/// <remarks>
/// Reports <c>Degraded</c>, not <c>Unhealthy</c>, when Mongo is unreachable. The
/// distinction is operationally important: the carrier catalog falls back to the
/// compiled-in panel and the comparison read model falls back to a SQL projection,
/// so the service still quotes correctly — just slower and without live pricing
/// configuration. Reporting Unhealthy would pull a working instance out of the load
/// balancer for a degradation.
/// </remarks>
public sealed class MongoHealthCheck(MongoIndexInitializer mongo) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connected = await mongo.CanConnectAsync(cancellationToken);

        return connected
            ? HealthCheckResult.Healthy("MongoDB is reachable.")
            : HealthCheckResult.Degraded(
                "MongoDB is unreachable. The carrier catalog and comparison read model " +
                "are falling back to SQL Server and the compiled-in panel.");
    }
}

/// <summary>
/// Readiness check for Azure Service Bus.
/// </summary>
/// <remarks>
/// <para>
/// Reports <c>Unhealthy</c> when the queue is unreachable, because unlike the
/// document store there is no fallback: without the queue, submitted quotes are
/// persisted but never underwritten, so they sit in <c>Pending</c> forever. An
/// instance in that state should not be taking traffic.
/// </para>
/// <para>
/// The probe is a <b>peek</b> on the queue, deliberately. Two alternatives were
/// rejected:
/// </para>
/// <list type="bullet">
///   <item><c>ServiceBusAdministrationClient.QueueExistsAsync</c> is the obvious
///         choice and does not work here: the local Service Bus emulator
///         implements only the AMQP data plane, not the management API, so every
///         check would fail against a perfectly healthy queue.</item>
///   <item>Sending a probe message would work but would put junk on the queue the
///         underwriting worker consumes.</item>
/// </list>
/// <para>
/// A peek neither locks nor removes a message, so it is safe to run on the probe
/// interval while the worker is consuming.
/// </para>
/// </remarks>
public sealed class ServiceBusHealthCheck(
    IServiceProvider services,
    IOptions<ServiceBusOptions> options,
    ILogger<ServiceBusHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var config = options.Value;

        if (config.IsDisabled)
        {
            return HealthCheckResult.Unhealthy(
                "Service Bus is not configured. Quotes will be accepted but never underwritten. " +
                "Set SERVICEBUS_CONNECTION_STRING.");
        }

        // Resolved optionally: the client is only registered when a connection
        // string is present, and the integration tests run without one.
        var client = services.GetService<ServiceBusClient>();
        if (client is null)
        {
            return HealthCheckResult.Unhealthy(
                "No Service Bus client is registered. Quotes will not be underwritten.");
        }

        try
        {
            // Created per check rather than held open. This runs on the probe
            // interval (seconds, not milliseconds), so the cost is irrelevant,
            // and a long-lived receiver here would compete with the worker.
            await using var receiver = client.CreateReceiver(config.QueueName);

            // Returns null on an empty queue and throws if the entity is missing
            // or the connection fails, which is exactly the distinction needed.
            await receiver.PeekMessageAsync(cancellationToken: cancellationToken);

            return HealthCheckResult.Healthy($"Service Bus queue '{config.QueueName}' is reachable.");
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            logger.LogError(
                ex,
                "Service Bus queue does not exist. Operation={Operation} Queue={Queue} Result={Result}",
                "ServiceBusHealthCheck", config.QueueName, "EntityNotFound");

            return HealthCheckResult.Unhealthy(
                $"Service Bus queue '{config.QueueName}' does not exist. " +
                "Check infra/servicebus/config.json.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Service Bus readiness check failed. Operation={Operation} Queue={Queue} Result={Result}",
                "ServiceBusHealthCheck", config.QueueName, "Unreachable");

            // The exception message is not returned to the caller: it can contain
            // the namespace endpoint. The description stays generic.
            return HealthCheckResult.Unhealthy("Service Bus is unreachable. See logs for detail.");
        }
    }
}

/// <summary>
/// Writes the readiness payload.
/// </summary>
/// <remarks>
/// Each check's status and duration are included because that is what makes a probe
/// useful during an incident — "ready is failing" is far less actionable than
/// "sql-server healthy 3ms, service-bus unhealthy 5002ms". The per-check
/// <i>descriptions</i> are written by the checks above and are deliberately free of
/// connection strings, hostnames and exception detail.
/// </remarks>
public static class HealthResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,
                description = entry.Value.Description
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }
}
