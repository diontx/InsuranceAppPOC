using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PolicyFlow.Domain.Messaging;

namespace PolicyFlow.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.EventType).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(1000);

        // The publisher's hot query is "unsent messages, oldest first". A filtered
        // index keeps it reading only the pending rows: once a message is sent it
        // leaves the index entirely, so the index stays small no matter how many
        // millions of processed rows accumulate behind it.
        builder.HasIndex(m => new { m.ProcessedUtc, m.OccurredUtc })
            .HasDatabaseName("IX_OutboxMessages_ProcessedUtc_OccurredUtc")
            .HasFilter("[ProcessedUtc] IS NULL");

        // Supports retention cleanup and troubleshooting by event type.
        builder.HasIndex(m => new { m.EventType, m.OccurredUtc })
            .HasDatabaseName("IX_OutboxMessages_EventType_OccurredUtc");
    }
}

public sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("ProcessedMessages");

        // Composite primary key. Keying on MessageId alone would mean a second,
        // unrelated consumer of the same event could never process it — a bug that
        // only appears the day a second subscriber is added.
        builder.HasKey(m => new { m.MessageId, m.Consumer });

        builder.Property(m => m.Consumer).HasMaxLength(100).IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(64).IsRequired();

        // The composite PK is already unique and is the index the dedup check uses.
        // This second index supports retention cleanup by age.
        builder.HasIndex(m => m.ProcessedUtc)
            .HasDatabaseName("IX_ProcessedMessages_ProcessedUtc");
    }
}
