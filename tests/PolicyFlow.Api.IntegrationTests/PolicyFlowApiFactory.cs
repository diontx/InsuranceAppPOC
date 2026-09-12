using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Carriers;
using PolicyFlow.Infrastructure.Persistence;

namespace PolicyFlow.Api.IntegrationTests;

/// <summary>
/// Hosts the real API in-process against a <b>real SQL Server</b>.
/// </summary>
/// <remarks>
/// <para>
/// Using real SQL Server is the entire point of this suite. The guarantees that
/// matter most in this system — payment idempotency, duplicate-bind prevention,
/// optimistic concurrency — are enforced by UNIQUE indexes and
/// <c>rowversion</c>, none of which the in-memory provider has. Testing them
/// against a fake would verify nothing and give false confidence.
/// </para>
/// <para>
/// MongoDB and Service Bus <i>are</i> substituted, for different reasons:
/// </para>
/// <list type="bullet">
///   <item>The document store is a cache whose fallback path these tests
///         deliberately exercise.</item>
///   <item>Service Bus would make every test wait on real message delivery. The
///         worker's consumer behaviour is covered by the application tests; here
///         the outbox is inspected directly, which is both faster and a stronger
///         assertion about what was committed.</item>
/// </list>
/// <para>
/// Each test class gets its own database, so tests cannot see each other's rows.
/// </para>
/// </remarks>
public sealed class PolicyFlowApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"PolicyFlowTests_{Guid.NewGuid():N}";

    /// <summary>
    /// SQL Server instance the tests run against. Overridable via
    /// <c>TEST_SQL_SERVER</c> for CI, where the container may not be on localhost.
    /// </summary>
    private static string SqlServerHost =>
        Environment.GetEnvironmentVariable("TEST_SQL_SERVER") ?? "localhost,1433";

    /// <summary>
    /// SA password, from the environment only. Never defaulted to a literal: a
    /// hard-coded fallback is how a test credential ends up committed.
    /// </summary>
    private static string SqlPassword =>
        Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD")
        ?? throw new InvalidOperationException(
            "MSSQL_SA_PASSWORD is not set. The integration tests require the SQL Server " +
            "container from docker-compose.yml. Run: docker compose up -d sql, then export " +
            "MSSQL_SA_PASSWORD from your .env file.");

    private string ConnectionString =>
        $"Server={SqlServerHost};Database={_databaseName};User Id=sa;Password={SqlPassword};" +
        "TrustServerCertificate=True;Encrypt=True;MultipleActiveResultSets=True";

    private string MasterConnectionString =>
        $"Server={SqlServerHost};Database=master;User Id=sa;Password={SqlPassword};" +
        "TrustServerCertificate=True;Encrypt=True";

    /// <summary>
    /// Publishes the host's configuration as process environment variables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This has to happen in the constructor, before the host starts.
    /// <c>Program.cs</c> is a top-level program that reads configuration
    /// <i>during service registration</i> (<c>AddInfrastructure</c> needs the
    /// connection string to register the <c>DbContext</c>), and
    /// <c>ConfigureAppConfiguration</c> from <see cref="WebApplicationFactory{T}"/>
    /// is applied after that point. Environment variables are the one source
    /// <c>Program.cs</c> reads early enough, via its own
    /// <c>AddEnvironmentVariables()</c> call.
    /// </para>
    /// <para>
    /// These are process-global, which is why the whole assembly shares a single
    /// factory through <c>IntegrationTestCollection</c>. Two factories would
    /// overwrite each other's connection string.
    /// </para>
    /// </remarks>
    public PolicyFlowApiFactory()
    {
        Environment.SetEnvironmentVariable("SQL_CONNECTION_STRING", ConnectionString);

        // Mongo is replaced by an in-memory stand-in below, but a syntactically
        // valid value is still required because DI reads it at registration time.
        Environment.SetEnvironmentVariable("MONGO_CONNECTION_STRING", "mongodb://localhost:27017");
        Environment.SetEnvironmentVariable("MONGO_DATABASE", "policyflow_tests");

        // Empty, so the API registers NoOpEventPublisher — which throws rather
        // than pretending to publish. Outbox rows therefore stay unprocessed,
        // which is exactly what these tests assert on.
        Environment.SetEnvironmentVariable("SERVICEBUS_CONNECTION_STRING", string.Empty);

        // A local symmetric key is acceptable because the host runs as
        // Development; SecurityExtensions refuses one in any other environment.
        Environment.SetEnvironmentVariable(
            "JWT_SIGNING_KEY", "integration-test-signing-key-at-least-32-bytes-long");
        Environment.SetEnvironmentVariable("JWT_ISSUER", "https://policyflow-local-idp");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "policyflow-api");
        Environment.SetEnvironmentVariable("ENABLE_DEV_TOKEN_ENDPOINT", "true");
        Environment.SetEnvironmentVariable("REQUIRE_AUTHENTICATION", "false");

        // High enough that the duplicate-payment tests, which deliberately hammer
        // one endpoint, are not rate limited.
        Environment.SetEnvironmentVariable("RATE_LIMIT_PERMIT_PER_MINUTE", "10000");

        // The factory migrates explicitly in InitializeAsync, so startup must not
        // race it.
        Environment.SetEnvironmentVariable("APPLY_MIGRATIONS_ON_STARTUP", "false");
        Environment.SetEnvironmentVariable("SEED_CARRIER_CATALOG", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // --- Replace the Mongo-backed catalog with the compiled-in panel, so
            //     tests do not need a Mongo container to rate a quote.
            services.RemoveAll<ICarrierCatalog>();
            services.AddScoped<ICarrierCatalog, InMemoryCarrierCatalog>();

            services.RemoveAll<IQuoteComparisonStore>();
            services.AddSingleton<IQuoteComparisonStore, InMemoryComparisonStore>();

            // --- Remove the outbox publisher. It would run on a timer during
            //     tests and throw continuously against the NoOp publisher,
            //     flooding the output for no benefit.
            services.RemoveAll<IHostedService>();
        });
    }

    // Explicit interface implementation. xUnit's IAsyncLifetime returns Task while
    // WebApplicationFactory.DisposeAsync returns ValueTask, so the two cannot share
    // a single public member.
    Task IAsyncLifetime.InitializeAsync() => InitializeDatabaseAsync();

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private async Task InitializeDatabaseAsync()
    {
        await CreateDatabaseAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PolicyFlowDbContext>();

        // Migrate(), not EnsureCreated(): the tests must run against the schema
        // the migrations actually produce, including every index and constraint.
        // EnsureCreated would build a schema from the model and could silently
        // diverge from what deploys to production.
        await db.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await DropDatabaseAsync();
        await base.DisposeAsync();
    }

    private async Task CreateDatabaseAsync()
    {
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        // A database name cannot be parameterised, so it has to be interpolated.
        // That is safe here only because the name is generated from a GUID and
        // never from input; the assertion below makes that a guarantee rather than
        // a comment, and QUOTENAME brackets the identifier for good measure.
        AssertSafeIdentifier(_databaseName);
        command.CommandText = $"""
            IF DB_ID(@dbName) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@dbName);
                EXEC sp_executesql @sql;
            END
            """;
        command.Parameters.AddWithValue("@dbName", _databaseName);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropDatabaseAsync()
    {
        try
        {
            // Pooled connections to the test database would block the DROP.
            SqlConnection.ClearAllPools();

            await using var connection = new SqlConnection(MasterConnectionString);
            await connection.OpenAsync();

            AssertSafeIdentifier(_databaseName);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(@dbName) IS NOT NULL
                BEGIN
                    DECLARE @sql nvarchar(max) =
                        N'ALTER DATABASE ' + QUOTENAME(@dbName) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;' +
                        N'DROP DATABASE ' + QUOTENAME(@dbName) + N';';
                    EXEC sp_executesql @sql;
                END
                """;
            command.Parameters.AddWithValue("@dbName", _databaseName);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Cleanup failure must not fail the test run. A leftover test database
            // is untidy, not incorrect, and masking a real assertion failure
            // behind a teardown error would be worse.
        }
    }

    /// <summary>
    /// Guards the one place in this solution where an identifier is interpolated
    /// into SQL. Only letters, digits and underscores are permitted, so the
    /// generated name cannot carry anything a database name should not.
    /// </summary>
    private static void AssertSafeIdentifier(string name)
    {
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException(
                $"Refusing to build SQL with the identifier '{name}': it contains characters " +
                "outside [A-Za-z0-9_].");
        }
    }

    /// <summary>Opens a scope and hands back the relational context.</summary>
    public async Task WithDbAsync(Func<PolicyFlowDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PolicyFlowDbContext>();
        await action(db);
    }

    public async Task<T> WithDbAsync<T>(Func<PolicyFlowDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PolicyFlowDbContext>();
        return await action(db);
    }

    /// <summary>
    /// Runs underwriting directly, standing in for the worker.
    /// </summary>
    /// <remarks>
    /// The same <c>UnderwriteQuoteHandler</c> the worker invokes, called in-process
    /// so the test does not have to wait for real Service Bus delivery. The
    /// consumer wrapper around it is covered by the application tests.
    /// </remarks>
    public async Task UnderwriteAsync(Guid quoteId)
    {
        using var scope = Services.CreateScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<Application.Quotes.UnderwriteQuoteHandler>();
        await handler.HandleAsync(quoteId);
    }
}

/// <summary>Serves the compiled-in carrier panel, bypassing MongoDB.</summary>
internal sealed class InMemoryCarrierCatalog : ICarrierCatalog
{
    public Task<IReadOnlyList<CarrierProfile>> GetActiveCarriersAsync(CancellationToken ct = default) =>
        Task.FromResult(CarrierPanel.All);

    public Task<CarrierProfile?> GetCarrierAsync(string carrierId, CancellationToken ct = default) =>
        Task.FromResult(CarrierPanel.Find(carrierId));

    public Task<int> SeedAsync(CancellationToken ct = default) =>
        Task.FromResult(CarrierPanel.All.Count);
}

/// <summary>In-memory stand-in for the Mongo comparison read model.</summary>
internal sealed class InMemoryComparisonStore : IQuoteComparisonStore
{
    private readonly Dictionary<Guid, QuoteComparisonDocument> _documents = [];
    private readonly Lock _gate = new();

    public Task UpsertAsync(QuoteComparisonDocument document, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _documents[document.QuoteId] = document;
        }

        return Task.CompletedTask;
    }

    public Task<QuoteComparisonDocument?> GetByQuoteIdAsync(Guid quoteId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_documents.TryGetValue(quoteId, out var doc) ? doc : null);
        }
    }

    public Task<QuoteComparisonDocument?> GetByQuoteNumberAsync(
        string quoteNumber,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _documents.Values.FirstOrDefault(d => d.QuoteNumber == quoteNumber));
        }
    }
}

/// <summary>Removes every registration of a service type.</summary>
internal static class ServiceCollectionExtensions
{
    public static IServiceCollection RemoveAll<TService>(this IServiceCollection services)
    {
        var registrations = services
            .Where(d => d.ServiceType == typeof(TService))
            .ToList();

        foreach (var registration in registrations)
        {
            services.Remove(registration);
        }

        return services;
    }
}
