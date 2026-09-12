using Microsoft.EntityFrameworkCore;
using PolicyFlow.Domain.Messaging;
using PolicyFlow.Domain.Payments;
using PolicyFlow.Domain.Policies;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Infrastructure.Persistence;

/// <summary>
/// The relational transactional store: SQL Server 2022.
/// </summary>
/// <remarks>
/// <para>
/// Holds everything that needs ACID guarantees — quotes, carrier offers, policies,
/// payments, the outbox and the processed-message ledger. The document store
/// (<c>PolicyFlow.Infrastructure.NoSql</c>) holds carrier configuration and the
/// comparison read model, which need neither transactions nor referential
/// integrity.
/// </para>
/// <para>
/// Entity configuration is split into <c>IEntityTypeConfiguration</c> classes
/// under <c>Persistence/Configurations</c> rather than piled into
/// <see cref="OnModelCreating"/>, so each table's keys, indexes and constraints
/// are reviewable in isolation.
/// </para>
/// <para>
/// Every query in this solution goes through LINQ or a parameterised
/// <c>FromSql</c> interpolated query. There is no string-concatenated SQL
/// anywhere, which is why SQL injection is not reachable.
/// </para>
/// </remarks>
public class PolicyFlowDbContext(DbContextOptions<PolicyFlowDbContext> options) : DbContext(options)
{
    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<CarrierOffer> CarrierOffers => Set<CarrierOffer>();

    public DbSet<Policy> Policies => Set<Policy>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    /// <summary>
    /// Monotonic counters backing quote and policy numbers. A dedicated table is
    /// used rather than a SQL SEQUENCE so the year-scoped reset
    /// (<c>Q-2026-000001</c>) is expressed in one place and stays portable.
    /// </summary>
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PolicyFlowDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// One row per (name, year) pair holding the last allocated number.
/// </summary>
/// <remarks>
/// Read and incremented under <c>UPDLOCK</c> inside the caller's transaction, so
/// two concurrent submissions cannot be handed the same quote number. The UNIQUE
/// index on <c>Quote.QuoteNumber</c> is the backstop if that ever fails.
/// </remarks>
public class NumberSequence
{
    public int Id { get; set; }

    /// <summary>Sequence name, e.g. <c>Quote</c> or <c>Policy</c>.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Calendar year the counter belongs to.</summary>
    public int Year { get; set; }

    /// <summary>Last number allocated. The next allocation is this plus one.</summary>
    public int LastValue { get; set; }
}
