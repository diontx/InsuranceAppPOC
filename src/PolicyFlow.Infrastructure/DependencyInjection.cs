using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Infrastructure.Messaging;
using PolicyFlow.Infrastructure.Persistence;
using PolicyFlow.Infrastructure.Repositories;

namespace PolicyFlow.Infrastructure;

/// <summary>
/// Wires the relational store and messaging transport. Shared by the API and the
/// worker so both see identical persistence behaviour.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // --- SQL Server.
        // The connection string comes from configuration, which is fed by
        // environment variables in Docker and user-secrets locally. It is never
        // present in appsettings.json.
        var connectionString = configuration.GetConnectionString("PolicyFlow")
            ?? configuration["SQL_CONNECTION_STRING"]
            ?? throw new InvalidOperationException(
                "No SQL Server connection string. Set ConnectionStrings:PolicyFlow or SQL_CONNECTION_STRING. " +
                "Copy .env.example to .env for local Docker development.");

        services.AddDbContext<PolicyFlowDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(PolicyFlowDbContext).Assembly.FullName);

                // SQL Server in a container is slow to accept connections on first
                // start and can drop them under load. Retrying transient errors here
                // avoids a startup race that looks like a code bug.
                sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);

                sql.CommandTimeout(30);
            });

            // Never enabled outside an explicit opt-in: detailed errors can include
            // parameter values, which may contain customer data.
            options.EnableDetailedErrors(false);
            options.EnableSensitiveDataLogging(false);
        });

        // --- Repositories and transaction boundary.
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<IPolicyRepository, PolicyRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IProcessedMessageRepository, ProcessedMessageRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // --- Service Bus.
        // Bound explicitly rather than through section binding so a bare
        // SERVICEBUS_* environment variable works identically to a nested
        // ServiceBus:* configuration key. Docker supplies the former.
        var section = configuration.GetSection(ServiceBusOptions.SectionName);
        var serviceBusConnection = section["ConnectionString"]
            ?? configuration["SERVICEBUS_CONNECTION_STRING"];

        services.Configure<ServiceBusOptions>(options =>
        {
            options.ConnectionString = serviceBusConnection ?? string.Empty;
            options.QueueName = section["QueueName"]
                ?? configuration["SERVICEBUS_QUEUE"]
                ?? "quote-submitted";
            options.OutboxBatchSize = ReadInt(section["OutboxBatchSize"], 20);
            options.OutboxPollSeconds = ReadInt(section["OutboxPollSeconds"], 2);
            options.MaxConcurrentCalls = ReadInt(section["MaxConcurrentCalls"], 4);
            options.MaxDeliveryCount = ReadInt(
                section["MaxDeliveryCount"] ?? configuration["SERVICEBUS_MAX_DELIVERY_COUNT"], 5);
        });

        if (!string.IsNullOrWhiteSpace(serviceBusConnection))
        {
            // ServiceBusClient is expensive to create and is thread-safe: one
            // singleton per application, as the SDK documents.
            services.AddSingleton(_ => new ServiceBusClient(serviceBusConnection, new ServiceBusClientOptions
            {
                // AMQP over TCP. The emulator does not support the WebSockets transport.
                TransportType = ServiceBusTransportType.AmqpTcp,
                RetryOptions = new ServiceBusRetryOptions
                {
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = 3,
                    Delay = TimeSpan.FromMilliseconds(800),
                    MaxDelay = TimeSpan.FromSeconds(10)
                }
            }));

            // Singleton, not scoped. Two reasons:
            //
            // 1. It creates a ServiceBusSender, which is itself expensive to
            //    construct and intended to be long-lived. A scoped publisher
            //    would build and tear one down on every outbox sweep — every two
            //    seconds, forever.
            // 2. It implements IAsyncDisposable only. A synchronous scope cannot
            //    dispose such a service and throws "type only implements
            //    IAsyncDisposable" when the scope ends. As a singleton it is owned
            //    by the root container, which disposes it asynchronously at
            //    shutdown.
            //
            // It has no scoped dependencies (ServiceBusClient is a singleton,
            // IOptions and ILogger are safe), so this is sound.
            services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();
        }
        else
        {
            // Degraded mode: quotes persist and the REST/SQL layers work, but
            // nothing underwrites them. Loud in the logs rather than silent.
            services.AddSingleton<IEventPublisher, NoOpEventPublisher>();
        }

        return services;
    }

    /// <summary>
    /// Registers the outbox sweep. Called by the API host only — running it in both
    /// the API and the worker would have two processes competing for the same rows.
    /// </summary>
    public static IServiceCollection AddOutboxPublisher(this IServiceCollection services)
    {
        services.AddHostedService<OutboxPublisherService>();
        return services;
    }

    /// <summary>Parses a configuration value, falling back when absent or malformed.</summary>
    private static int ReadInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
