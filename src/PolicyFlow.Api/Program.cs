using System.Reflection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using PolicyFlow.Api.Health;
using PolicyFlow.Api.Middleware;
using PolicyFlow.Api.Security;
using PolicyFlow.Application;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Infrastructure;
using PolicyFlow.Infrastructure.NoSql;
using PolicyFlow.Infrastructure.NoSql.Persistence;
using PolicyFlow.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

// ============================================================================
// PolicyFlow API host.
//
// Composition root only: it wires layers and configures the pipeline. No business
// logic lives here.
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ----------------------------------------------------------------------------
// Configuration
// ----------------------------------------------------------------------------
// Environment variables are added last so they win. Docker Compose supplies every
// secret that way, which is why no connection string or key appears in
// appsettings.json.
builder.Configuration.AddEnvironmentVariables();

// ----------------------------------------------------------------------------
// Structured logging
// ----------------------------------------------------------------------------
// Structured, not string-formatted: "quote {QuoteId} took {ElapsedMs}ms" is
// queryable by field, which is the difference between finding a slow carrier in
// one query and grepping a log file.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "PolicyFlow.Api")
    .WriteTo.Console(
        outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

// ----------------------------------------------------------------------------
// Security configuration
// ----------------------------------------------------------------------------
var security = new SecurityOptions
{
    Issuer = builder.Configuration["JWT_ISSUER"] ?? "https://policyflow-local-idp",
    Audience = builder.Configuration["JWT_AUDIENCE"] ?? "policyflow-api",
    SigningKey = builder.Configuration["JWT_SIGNING_KEY"] ?? string.Empty,
    Authority = builder.Configuration["JWT_AUTHORITY"],
    TokenLifetimeMinutes = int.TryParse(builder.Configuration["JWT_TOKEN_LIFETIME_MINUTES"], out var lifetime)
        ? lifetime
        : 60,
    EnableDevTokenEndpoint = bool.TryParse(builder.Configuration["ENABLE_DEV_TOKEN_ENDPOINT"], out var devToken)
        && devToken,
    AllowedOrigins = (builder.Configuration["CORS_ALLOWED_ORIGINS"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    RequireAuthentication = bool.TryParse(builder.Configuration["REQUIRE_AUTHENTICATION"], out var requireAuth)
        && requireAuth,
    RateLimitPermitPerMinute = int.TryParse(builder.Configuration["RATE_LIMIT_PERMIT_PER_MINUTE"], out var permit)
        ? permit
        : 120,
    RateLimitQueueLimit = int.TryParse(builder.Configuration["RATE_LIMIT_QUEUE_LIMIT"], out var queue)
        ? queue
        : 10
};

builder.Services.Configure<SecurityOptions>(options =>
{
    options.Issuer = security.Issuer;
    options.Audience = security.Audience;
    options.SigningKey = security.SigningKey;
    options.Authority = security.Authority;
    options.TokenLifetimeMinutes = security.TokenLifetimeMinutes;
    options.EnableDevTokenEndpoint = security.EnableDevTokenEndpoint;
    options.AllowedOrigins = security.AllowedOrigins;
    options.RequireAuthentication = security.RequireAuthentication;
    options.RateLimitPermitPerMinute = security.RateLimitPermitPerMinute;
    options.RateLimitQueueLimit = security.RateLimitQueueLimit;
});

// Throws on an unsafe configuration combination. See SecurityExtensions.
builder.Services.AddPolicyFlowSecurity(security, builder.Environment);

// ----------------------------------------------------------------------------
// Application and infrastructure layers
// ----------------------------------------------------------------------------
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddNoSqlInfrastructure(builder.Configuration);

// The API owns the outbox sweep. Registering it in the worker too would have two
// processes competing for the same rows.
builder.Services.AddOutboxPublisher();

// Correlation context is scoped: one per request, shared by every handler in it.
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());

// ----------------------------------------------------------------------------
// MVC and error handling
// ----------------------------------------------------------------------------
builder.Services
    .AddControllers(options =>
    {
        // Fail on an unknown route rather than falling through to a 404 that looks
        // like a missing resource.
        options.ReturnHttpNotAcceptable = true;
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
        // Omit nulls: the comparison payload has many optional fields and this
        // meaningfully shrinks it for a mobile client.
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Model-binding failures also return ProblemDetails, so a client sees one error
// shape for both binding and business failures.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred",
            Type = "https://policyflow.example/problems/validation",
            Instance = context.HttpContext.Request.Path
        };

        problem.Extensions["correlationId"] = context.HttpContext.RequestServices
            .GetService<ICorrelationContext>()?.CorrelationId;

        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    };
});

// ----------------------------------------------------------------------------
// Health checks
// ----------------------------------------------------------------------------
// Two probes with different jobs. `live` answers "is the process up" and must not
// touch dependencies — a liveness probe that checks the database will restart a
// healthy container during a database blip. `ready` answers "can this instance
// serve traffic" and does check them, so a starting instance is kept out of the
// load balancer until its dependencies are reachable.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PolicyFlowDbContext>(
        name: "sql-server",
        tags: ["ready"])
    .AddCheck<MongoHealthCheck>(
        name: "mongodb",
        tags: ["ready"])
    .AddCheck<ServiceBusHealthCheck>(
        name: "service-bus",
        tags: ["ready"]);

// ----------------------------------------------------------------------------
// OpenAPI
// ----------------------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "PolicyFlow API",
        Version = "v1",
        Description =
            "Vendor-neutral auto-insurance modernization POC. Shops a quote against five " +
            "**fictional** carriers asynchronously via Azure Service Bus, stores transactional " +
            "data in SQL Server (EF Core) and the carrier catalog plus comparison read models in " +
            "MongoDB (EF Core). Includes a legacy Web Forms compatibility seam.\n\n" +
            "**All carriers, rating factors and premiums in this API are invented for " +
            "demonstration and do not reflect real insurance pricing.**"
    });

    // Documents the Idempotency-Key and X-Correlation-Id headers on every operation
    // that accepts them, so the contract is discoverable rather than tribal
    // knowledge.
    options.OperationFilter<HeaderDocumentationFilter>();

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description =
            "JWT bearer token. In local development, obtain one from POST /api/auth/dev-token."
    });

    // OpenAPI.NET v2 references a declared scheme by id through a dedicated
    // reference type rather than by populating Reference on the scheme itself.
    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", null, null)] = new List<string>()
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

// ----------------------------------------------------------------------------
// Forwarded headers
// ----------------------------------------------------------------------------
// Behind the nginx container that serves the SPA, so the original scheme and
// client IP must be recovered — otherwise rate limiting partitions every caller
// into the proxy's single IP.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Cleared because the proxy runs on a Docker bridge network whose address is
    // not known ahead of time. Safe here because the API is not internet-facing in
    // this POC; a real deployment must list its actual proxies, or a client can
    // spoof X-Forwarded-For and evade the rate limiter.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// ============================================================================
// Pipeline. Order matters.
// ============================================================================

// 1. Forwarded headers first: everything downstream should see the real scheme
//    and client IP.
app.UseForwardedHeaders();

// 2. Security headers before anything can produce a response body.
app.UseMiddleware<SecurityHeadersMiddleware>();

// 3. Correlation id before the exception handler, so a failure still reports one.
app.UseMiddleware<CorrelationIdMiddleware>();

// 4. Exception handler, converting anything unhandled into ProblemDetails.
app.UseExceptionHandler();

// 5. HTTPS redirection and HSTS outside Development. In Development the API is
//    reached over plain HTTP inside the Docker network, where forcing HTTPS would
//    break container-to-container calls for no security gain.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// 6. Request logging. After correlation so each line carries the id.
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0}ms";

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("CorrelationId",
            httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
        diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString());
        // Deliberately NOT logged: the Authorization header, the Idempotency-Key
        // (client-chosen, could contain anything) and the request body.
    };
});

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ----------------------------------------------------------------------------
// OpenAPI, Development only
// ----------------------------------------------------------------------------
// Not exposed in other environments: the schema is a map of the attack surface,
// and Swagger UI would need its own CSP exemption.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "PolicyFlow API v1");
        options.DocumentTitle = "PolicyFlow API";
    });
}

// ----------------------------------------------------------------------------
// Health endpoints
// ----------------------------------------------------------------------------
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    // No dependency checks: only whether this process can serve a request.
    Predicate = _ => false
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync
}).AllowAnonymous();

// ----------------------------------------------------------------------------
// Controllers
// ----------------------------------------------------------------------------
var controllers = app.MapControllers();
controllers.RequireRateLimiting(SecurityExtensions.RateLimitPolicyName);

// When REQUIRE_AUTHENTICATION is off (the default for the demo), per-action
// [Authorize] attributes are satisfied by a permissive fallback so the flow can be
// walked without a token step. The policies themselves are still registered and
// still enforced when a token IS present, so turning the flag on needs no code
// change. docs/SECURITY.md explains the trade-off.
if (!security.RequireAuthentication)
{
    app.Logger.LogWarning(
        "REQUIRE_AUTHENTICATION is false: endpoint authorization is not enforced. " +
        "This is intended for the local demo only. Operation={Operation} Result={Result}",
        "Startup", "AuthorizationRelaxed");
}

// ----------------------------------------------------------------------------
// Startup tasks
// ----------------------------------------------------------------------------
await StartupTasks.RunAsync(app);

app.Run();

/// <summary>
/// One-time startup work: relational migrations and NoSQL seeding/indexing.
/// </summary>
internal static class StartupTasks
{
    public static async Task RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // --- Relational migrations.
        // Gated on an explicit flag AND the environment, so a production deployment
        // cannot silently alter its own schema at startup. Migrations there belong
        // in a controlled pipeline step with a rollback plan.
        var applyMigrations = bool.TryParse(configuration["APPLY_MIGRATIONS_ON_STARTUP"], out var apply) && apply;

        if (applyMigrations && app.Environment.IsDevelopment())
        {
            var db = scope.ServiceProvider.GetRequiredService<PolicyFlowDbContext>();

            try
            {
                // Migrate(), never EnsureCreated(): EnsureCreated bypasses the
                // migration history table, so the schema it builds can never be
                // upgraded afterwards.
                await db.Database.MigrateAsync();

                logger.LogInformation(
                    "Relational migrations applied. Operation={Operation} Result={Result}",
                    "ApplyMigrations", "Applied");
            }
            catch (Exception ex)
            {
                // Fatal: the API cannot serve traffic without its schema.
                logger.LogCritical(
                    ex,
                    "Relational migrations failed. Operation={Operation} Result={Result}",
                    "ApplyMigrations", "Failed");
                throw;
            }
        }
        else if (applyMigrations)
        {
            logger.LogWarning(
                "APPLY_MIGRATIONS_ON_STARTUP is set but the environment is {Environment}. " +
                "Automatic migration is restricted to Development. Operation={Operation} Result={Result}",
                app.Environment.EnvironmentName, "ApplyMigrations", "Skipped");
        }

        // --- MongoDB indexes. Idempotent, non-fatal on failure.
        var indexes = scope.ServiceProvider.GetRequiredService<MongoIndexInitializer>();
        await indexes.EnsureIndexesAsync();

        // --- Carrier catalog seed.
        var seedCatalog = bool.TryParse(configuration["SEED_CARRIER_CATALOG"], out var seed) && seed;

        if (seedCatalog)
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ICarrierCatalog>();

            try
            {
                await catalog.SeedAsync();
            }
            catch (Exception ex)
            {
                // Non-fatal. MongoCarrierCatalog falls back to the compiled-in panel,
                // so the demo still rates quotes correctly without the catalog.
                logger.LogError(
                    ex,
                    "Carrier catalog seeding failed; the compiled-in panel will be used. " +
                    "Operation={Operation} Result={Result}",
                    "SeedCarrierCatalog", "Failed");
            }
        }
    }
}
