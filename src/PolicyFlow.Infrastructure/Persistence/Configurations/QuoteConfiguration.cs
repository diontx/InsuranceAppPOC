using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Infrastructure.Persistence.Configurations;

/// <summary>
/// Relational mapping for <see cref="Quote"/> and its owned offer collection.
/// </summary>
/// <remarks>
/// SQL Server is not treated as a bucket for objects here. The schema itself
/// enforces business integrity: a UNIQUE quote number, a <c>rowversion</c> for
/// optimistic concurrency, and indexes shaped by the queries the application
/// actually runs.
/// </remarks>
public sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> builder)
    {
        builder.ToTable("Quotes");

        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();

        builder.Property(q => q.QuoteNumber)
            .HasMaxLength(20)
            .IsRequired();

        // Unique: the customer-facing identifier must never collide, even if the
        // sequence allocator is ever wrong.
        builder.HasIndex(q => q.QuoteNumber)
            .IsUnique()
            .HasDatabaseName("UX_Quotes_QuoteNumber");

        builder.Property(q => q.CustomerName)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(q => q.State)
            .HasMaxLength(2)
            .IsFixedLength()
            .IsRequired();

        builder.Property(q => q.VehicleMake).HasMaxLength(50).IsRequired();
        builder.Property(q => q.VehicleModel).HasMaxLength(50).IsRequired();

        // Money is decimal(18,2), never float. Binary floating point cannot
        // represent cents exactly, and premiums are summed and compared.
        builder.Property(q => q.AnnualPremium).HasPrecision(18, 2);

        builder.Property(q => q.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(q => q.BestCarrierId).HasMaxLength(50);

        builder.Property(q => q.BestCoverageTier)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(q => q.DecisionReason).HasMaxLength(500);

        builder.Property(q => q.Channel)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(q => q.CorrelationId).HasMaxLength(64).IsRequired();

        // Underwriter review. Lengths come from the aggregate so the schema and the
        // domain invariant cannot drift apart.
        builder.Property(q => q.ReviewedBy).HasMaxLength(Quote.MaxReviewerLength);
        builder.Property(q => q.ReviewNote).HasMaxLength(Quote.MaxReviewNoteLength);

        // rowversion: SQL Server stamps this on every UPDATE. EF Core includes it
        // in the WHERE clause, so a lost update raises DbUpdateConcurrencyException
        // instead of silently overwriting a concurrent change.
        builder.Property(q => q.RowVersion)
            .IsRowVersion()
            .IsConcurrencyToken();

        // Supports the worker sweeping for quotes stuck in a non-terminal state, and
        // the underwriter work queue (Status IN (Referral, FollowUp) ORDER BY
        // CreatedUtc), which is the same seek-then-ordered-scan shape.
        builder.HasIndex(q => new { q.Status, q.CreatedUtc })
            .HasDatabaseName("IX_Quotes_Status_CreatedUtc");

        // Supports the mobile self-service lookup (quote number + surname). The
        // quote number index above already covers the equality predicate; this
        // covering index avoids a key lookup for the name check.
        builder.HasIndex(q => new { q.QuoteNumber, q.CustomerName })
            .HasDatabaseName("IX_Quotes_QuoteNumber_CustomerName");

        // Offers are part of the quote aggregate: they are created with it, read
        // with it, and have no independent lifetime. Cascade delete matches that.
        builder.HasMany(q => q.Offers)
            .WithOne(o => o.Quote!)
            .HasForeignKey(o => o.QuoteId)
            .OnDelete(DeleteBehavior.Cascade);

        // The aggregate exposes offers through a read-only collection, so EF Core
        // is pointed at the backing field.
        builder.Metadata
            .FindNavigation(nameof(Quote.Offers))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class CarrierOfferConfiguration : IEntityTypeConfiguration<CarrierOffer>
{
    public void Configure(EntityTypeBuilder<CarrierOffer> builder)
    {
        builder.ToTable("CarrierOffers");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.CarrierId).HasMaxLength(50).IsRequired();
        builder.Property(o => o.CarrierName).HasMaxLength(120).IsRequired();

        builder.Property(o => o.CoverageTier)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(o => o.AnnualPremium).HasPrecision(18, 2).IsRequired();
        builder.Property(o => o.MonthlyPremium).HasPrecision(18, 2).IsRequired();
        builder.Property(o => o.Deductible).HasPrecision(18, 2).IsRequired();
        builder.Property(o => o.DiscountAmount).HasPrecision(18, 2).IsRequired();

        builder.Property(o => o.FinancialStrengthRating).HasMaxLength(10).IsRequired();
        builder.Property(o => o.AppliedDiscounts).HasMaxLength(500);

        // The rating trace can run to a dozen lines; nvarchar(max) is appropriate
        // and it is never filtered on.
        builder.Property(o => o.RatingTrace);

        // The comparison screen reads every offer for one quote, cheapest first.
        builder.HasIndex(o => new { o.QuoteId, o.AnnualPremium })
            .HasDatabaseName("IX_CarrierOffers_QuoteId_AnnualPremium");

        // One offer per carrier per tier per quote. Without this, a bug that
        // re-rated a quote would silently double the panel.
        builder.HasIndex(o => new { o.QuoteId, o.CarrierId, o.CoverageTier })
            .IsUnique()
            .HasDatabaseName("UX_CarrierOffers_Quote_Carrier_Tier");
    }
}
