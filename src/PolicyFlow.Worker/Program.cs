using PolicyFlow.Application;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Infrastructure;
using PolicyFlow.Infrastructure.NoSql;
using PolicyFlow.Worker;
using Serilog;
using Serilog.Events;

// ============================================================================
// PolicyFlow underwriting worker.
//
// Consumes QuoteSubmitted from Service Bus, verifies prior insurance, and shops
// the quote against the five-carrier panel.
//
// Runs as a separate process from the API on purpose. Underwriting is slow and
// bursty; the API must stay responsive. Separating them means the worker can be
// scaled, restarted or deployed independently, and a flood of quotes queues up
// rather than degrading the customer-facing API.
// ============================================================================

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables();

// Logging comes from Serilog.Extensions.Hosting, deliberately NOT
// Serilog.AspNetCore. The latter carries a framework reference to
// Microsoft.AspNetCore.App, which would force this image onto the aspnet base
// (~80MB larger) and ship a web stack into a process that serves no HTTP.
//
// The Service property prefixes every line, which is how the demo traces one
// correlation id across the API and worker consoles side by side.
builder.Services.AddSerilog((services, configuration) => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("Azure.Messaging.ServiceBus", LogEventLevel.Warning)
    .MinimumLevel.Override("Azure.Core", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "PolicyFlow.Worker")
    .WriteTo.Console(
        outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

// Same application and infrastructure registrations as the API. That is the point:
// the worker runs the identical domain and application code, so rating cannot
// drift between the synchronous and asynchronous paths.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddNoSqlInfrastructure(builder.Configuration);

// The correlation id in the worker comes from the message, not an HTTP header.
builder.Services.AddScoped<ICorrelationContext, WorkerCorrelationContext>();

builder.Services.AddHostedService<UnderwritingWorker>();

// Deliberately NOT registered here: AddOutboxPublisher(). The API owns the outbox
// sweep. Running it in both processes would have two publishers competing for the
// same rows and double-sending messages.

var host = builder.Build();

host.Run();

/// <summary>
/// Correlation context for the worker.
/// </summary>
/// <remarks>
/// The worker has no HTTP request to read a header from. The real correlation id
/// arrives on the Service Bus message and is applied by the logging scope in
/// <see cref="UnderwritingWorker"/>; this type supplies a fallback for any code
/// path that resolves the interface outside message handling, so nothing can
/// depend on a null id.
/// </remarks>
internal sealed class WorkerCorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; } = $"worker-{Guid.NewGuid()}";
}
