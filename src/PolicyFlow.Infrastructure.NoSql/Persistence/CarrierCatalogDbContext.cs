using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;
using PolicyFlow.Infrastructure.NoSql.Documents;

namespace PolicyFlow.Infrastructure.NoSql.Persistence;

/// <summary>
/// EF Core <c>DbContext</c> over MongoDB, using the official
/// <c>MongoDB.EntityFrameworkCore</c> provider.
/// </summary>
/// <remarks>
/// <para>
/// Two <c>DbContext</c> types, one per store, is deliberate. They have different
/// consistency models — SQL Server gives ACID transactions and referential
/// integrity, Mongo gives flexible documents and cheap denormalised reads — and
/// merging them would let a relational entity accidentally acquire a Mongo
/// dependency, or let a handler assume a transaction it does not have.
/// </para>
/// <para>
/// EF Core over Mongo intentionally does not support relational concepts:
/// migrations, foreign keys or <c>rowversion</c>. Collections are created on
/// first write and indexes are created explicitly at startup by
/// <see cref="MongoIndexInitializer"/>.
/// </para>
/// <para>
/// <b>Nested collections must be declared with <c>OwnsMany</c>.</b> Without it
/// the provider treats a collection of complex types as an unmapped navigation
/// and model validation fails at first use. <c>OwnsMany</c> tells it these are
/// embedded sub-documents stored inside the parent document rather than
/// separate entities with their own collection — which is the whole point of
/// using a document store here.
/// </para>
/// </remarks>
public class CarrierCatalogDbContext : DbContext
{
    public CarrierCatalogDbContext(DbContextOptions<CarrierCatalogDbContext> options)
        : base(options)
    {
        // ====================================================================
        // Transactions are disabled for this context, deliberately.
        //
        // The MongoDB EF Core provider wraps SaveChanges in a transaction by
        // default, and MongoDB only supports transactions on a replica set or
        // sharded cluster. Against the standalone `mongo` container this throws
        // "Standalone servers do not support transactions" on every write.
        //
        // Two ways out. Running Mongo as a single-node replica set would restore
        // transactions but adds rs.initiate() orchestration and a slower, more
        // fragile container startup. Disabling them is correct here instead,
        // because of what this store actually holds:
        //
        //   * Every SaveChanges on this context writes exactly ONE document — a
        //     carrier upsert or a comparison upsert. A single-document write in
        //     MongoDB is already atomic, so a transaction adds nothing.
        //   * Catalog seeding writes five documents in one call, but it is
        //     idempotent by carrier id: a partial application is completed by the
        //     next run rather than leaving anything inconsistent.
        //   * Neither collection is a source of truth. The relational store is,
        //     and it has real ACID transactions.
        //
        // A document store holding money or multi-document invariants would need
        // the replica set. This one does not.
        // ====================================================================
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
    }

    /// <summary>Carrier rating configuration. Read on every underwriting run.</summary>
    public DbSet<CarrierDocument> Carriers => Set<CarrierDocument>();

    /// <summary>Denormalised comparison read models, one per quote.</summary>
    public DbSet<QuoteComparisonDoc> QuoteComparisons => Set<QuoteComparisonDoc>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // --- Carrier catalog.
        modelBuilder.Entity<CarrierDocument>(carrier =>
        {
            carrier.ToCollection("carriers");

            // Embedded sub-documents. Each lives inside the carrier document, so a
            // carrier is still a single read.
            carrier.OwnsMany(c => c.Coverages);
            carrier.OwnsMany(c => c.Discounts);

            // StateFactors and TierFactors are Dictionary<string, double>. Primitive
            // values map as a BSON sub-document without any configuration, which is
            // why they can stay dictionaries while Coverages cannot.
        });

        // --- Comparison read model.
        modelBuilder.Entity<QuoteComparisonDoc>(comparison =>
        {
            comparison.ToCollection("quote_comparisons");

            comparison.OwnsMany(d => d.Offers, offer =>
            {
                // Nested one level deeper: string lists inside an owned entity.
                // These are primitive collections and need no further mapping.
                offer.Property(o => o.AppliedDiscounts);
                offer.Property(o => o.RatingTrace);
            });

            comparison.OwnsMany(d => d.Declinations);
        });

        base.OnModelCreating(modelBuilder);
    }
}
