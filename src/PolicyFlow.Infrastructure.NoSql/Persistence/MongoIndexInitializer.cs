using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using PolicyFlow.Infrastructure.NoSql.Documents;

namespace PolicyFlow.Infrastructure.NoSql.Persistence;

/// <summary>
/// Creates the MongoDB indexes the application queries rely on.
/// </summary>
/// <remarks>
/// <para>
/// EF Core migrations do not apply to the Mongo provider, and Mongo will happily
/// serve an unindexed collection with a full scan — which is why a missing index
/// here is a performance bug that only shows up under data volume, never as an
/// error. Creating them explicitly at startup makes them part of the deployment
/// rather than something an operator remembers.
/// </para>
/// <para>
/// <c>CreateMany</c> is idempotent: creating an index that already exists with the
/// same specification is a no-op, so this is safe on every container start.
/// </para>
/// </remarks>
public sealed class MongoIndexInitializer(
    IMongoDatabase database,
    ILogger<MongoIndexInitializer> logger)
{
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        try
        {
            var carriers = database.GetCollection<CarrierDocument>("carriers");

            await carriers.Indexes.CreateManyAsync(
                [
                    // Unique: the catalog key is the identity the rating engine
                    // resolves carriers by, so a duplicate would make pricing
                    // non-deterministic.
                    new CreateIndexModel<CarrierDocument>(
                        Builders<CarrierDocument>.IndexKeys.Ascending(c => c.CarrierId),
                        new CreateIndexOptions { Unique = true, Name = "ux_carriers_carrierId" }),

                    // Supports the panel read: active carriers in sort order.
                    new CreateIndexModel<CarrierDocument>(
                        Builders<CarrierDocument>.IndexKeys
                            .Ascending(c => c.IsActive)
                            .Ascending(c => c.SortOrder),
                        new CreateIndexOptions { Name = "ix_carriers_active_sortOrder" })
                ],
                ct);

            var comparisons = database.GetCollection<QuoteComparisonDoc>("quote_comparisons");

            await comparisons.Indexes.CreateManyAsync(
                [
                    // Unique: one comparison document per quote. The upsert relies
                    // on this to avoid accumulating duplicates on re-underwriting.
                    new CreateIndexModel<QuoteComparisonDoc>(
                        Builders<QuoteComparisonDoc>.IndexKeys.Ascending(d => d.QuoteId),
                        new CreateIndexOptions { Unique = true, Name = "ux_quote_comparisons_quoteId" }),

                    // Supports the mobile lookup path, which searches by the
                    // customer-facing quote number rather than the GUID.
                    new CreateIndexModel<QuoteComparisonDoc>(
                        Builders<QuoteComparisonDoc>.IndexKeys.Ascending(d => d.QuoteNumber),
                        new CreateIndexOptions { Name = "ix_quote_comparisons_quoteNumber" }),

                    // TTL index: comparison documents are a cache and expire after
                    // 30 days. Without it the collection grows without bound, since
                    // nothing in the application ever deletes a read model.
                    new CreateIndexModel<QuoteComparisonDoc>(
                        Builders<QuoteComparisonDoc>.IndexKeys.Ascending(d => d.GeneratedUtc),
                        new CreateIndexOptions
                        {
                            Name = "ttl_quote_comparisons_generatedUtc",
                            ExpireAfter = TimeSpan.FromDays(30)
                        })
                ],
                ct);

            logger.LogInformation(
                "MongoDB indexes ensured. Operation={Operation} Database={Database} Result={Result}",
                "EnsureMongoIndexes", database.DatabaseNamespace.DatabaseName, "Ensured");
        }
        catch (Exception ex)
        {
            // Non-fatal: the application still functions, just with slower reads.
            // Failing startup over an index would take the service down for a
            // degradation rather than an outage.
            logger.LogError(
                ex,
                "MongoDB index creation failed; document reads will be slower than intended. " +
                "Operation={Operation} Result={Result}",
                "EnsureMongoIndexes", "Failed");
        }
    }

    /// <summary>
    /// Verifies connectivity for the readiness probe. Uses the cheap <c>ping</c>
    /// admin command rather than a collection query.
    /// </summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
