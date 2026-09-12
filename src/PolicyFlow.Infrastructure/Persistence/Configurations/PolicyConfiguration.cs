using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PolicyFlow.Domain.Payments;
using PolicyFlow.Domain.Policies;

namespace PolicyFlow.Infrastructure.Persistence.Configurations;

public sealed class PolicyConfiguration : IEntityTypeConfiguration<Policy>
{
    public void Configure(EntityTypeBuilder<Policy> builder)
    {
        builder.ToTable("Policies");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.PolicyNumber).HasMaxLength(20).IsRequired();

        builder.HasIndex(p => p.PolicyNumber)
            .IsUnique()
            .HasDatabaseName("UX_Policies_PolicyNumber");

        // THE duplicate-binding guarantee. A quote can back at most one policy.
        // The application also pre-reads for a friendly 409, but two concurrent
        // requests can both pass that read — this index is what actually holds.
        builder.HasIndex(p => p.QuoteId)
            .IsUnique()
            .HasDatabaseName("UX_Policies_QuoteId");

        builder.Property(p => p.CarrierId).HasMaxLength(50).IsRequired();
        builder.Property(p => p.CarrierName).HasMaxLength(120).IsRequired();

        builder.Property(p => p.CoverageTier)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.InsuredName).HasMaxLength(120).IsRequired();

        builder.Property(p => p.State)
            .HasMaxLength(2)
            .IsFixedLength()
            .IsRequired();

        builder.Property(p => p.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.AnnualPremium).HasPrecision(18, 2).IsRequired();
        builder.Property(p => p.MonthlyPremium).HasPrecision(18, 2).IsRequired();
        builder.Property(p => p.Deductible).HasPrecision(18, 2).IsRequired();

        // DateOnly maps to SQL Server `date`: no time component, no time zone
        // ambiguity on a policy term boundary.
        builder.Property(p => p.EffectiveDate).HasColumnType("date").IsRequired();
        builder.Property(p => p.ExpirationDate).HasColumnType("date").IsRequired();

        builder.Property(p => p.CorrelationId).HasMaxLength(64).IsRequired();

        builder.Property(p => p.RowVersion)
            .IsRowVersion()
            .IsConcurrencyToken();

        builder.HasOne(p => p.Quote!)
            .WithMany()
            .HasForeignKey(p => p.QuoteId)
            // Restrict, not Cascade: deleting a quote must never silently delete a
            // bound policy. A bound policy is a financial obligation.
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Payments)
            .WithOne(pay => pay.Policy!)
            .HasForeignKey(pay => pay.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Metadata
            .FindNavigation(nameof(Policy.Payments))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // TotalPaid and BalanceDue are computed from the payment collection in the
        // domain, not stored. Telling EF Core to ignore them keeps the schema from
        // carrying a derivable value that could drift out of agreement.
        builder.Ignore(p => p.TotalPaid);
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Amount).HasPrecision(18, 2).IsRequired();

        builder.Property(p => p.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(p => p.ExternalTransactionId).HasMaxLength(64).IsRequired();
        builder.Property(p => p.CorrelationId).HasMaxLength(64).IsRequired();

        // ====================================================================
        // THE idempotency guarantee.
        // --------------------------------------------------------------------
        // Scope is (PolicyId, IdempotencyKey), not the key alone. Keys are chosen
        // by clients: two customers could independently generate the same key, and
        // a global unique index would then reject a legitimate second payment. The
        // key only needs to be unique within the resource it acts on.
        //
        // This index is what makes duplicate payment impossible. Application-level
        // checks are an optimisation on top of it, not a substitute: they cannot
        // survive two concurrent requests on two servers, and this can.
        // ====================================================================
        builder.HasIndex(p => new { p.PolicyId, p.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_Payments_PolicyId_IdempotencyKey");

        builder.HasIndex(p => new { p.PolicyId, p.CreatedUtc })
            .HasDatabaseName("IX_Payments_PolicyId_CreatedUtc");
    }
}

public sealed class NumberSequenceConfiguration : IEntityTypeConfiguration<NumberSequence>
{
    public void Configure(EntityTypeBuilder<NumberSequence> builder)
    {
        builder.ToTable("NumberSequences");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedOnAdd();

        builder.Property(s => s.Name).HasMaxLength(40).IsRequired();

        // One counter per sequence per year. The unique index is what lets the
        // allocator use a single UPDLOCK'd row as the serialisation point.
        builder.HasIndex(s => new { s.Name, s.Year })
            .IsUnique()
            .HasDatabaseName("UX_NumberSequences_Name_Year");

        // Seeded so the first allocation is an UPDATE on an existing row rather
        // than a racy INSERT.
        builder.HasData(
            new NumberSequence { Id = 1, Name = "Quote", Year = 2026, LastValue = 0 },
            new NumberSequence { Id = 2, Name = "Policy", Year = 2026, LastValue = 0 });
    }
}
