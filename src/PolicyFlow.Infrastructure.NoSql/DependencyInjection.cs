using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Infrastructure.NoSql.Persistence;

namespace PolicyFlow.Infrastructure.NoSql;

/// <summary>
/// Wires the MongoDB document store and its EF Core context.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNoSqlInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Mongo")
            ?? configuration["MONGO_CONNECTION_STRING"]
            ?? throw new InvalidOperationException(
                "No MongoDB connection string. Set ConnectionStrings:Mongo or MONGO_CONNECTION_STRING. " +
                "Copy .env.example to .env for local Docker development.");

        var databaseName = configuration["MONGO_DATABASE"] ?? "policyflow_catalog";

        // MongoClient is thread-safe and owns an internal connection pool, so the
        // driver documents one singleton per application. Creating one per request
        // exhausts connections under load.
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));

        services.AddSingleton(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        services.AddDbContext<CarrierCatalogDbContext>(options =>
            options.UseMongoDB(
                connectionString,
                databaseName));

        services.AddScoped<ICarrierCatalog, MongoCarrierCatalog>();
        services.AddScoped<IQuoteComparisonStore, MongoQuoteComparisonStore>();
        services.AddScoped<MongoIndexInitializer>();

        return services;
    }
}
