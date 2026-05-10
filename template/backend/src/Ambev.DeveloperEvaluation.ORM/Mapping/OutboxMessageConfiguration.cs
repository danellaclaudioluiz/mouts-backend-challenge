using Ambev.DeveloperEvaluation.ORM.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ambev.DeveloperEvaluation.ORM.Mapping;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.EventType).IsRequired().HasMaxLength(200);
        builder.Property(m => m.Payload).IsRequired().HasColumnType("jsonb");
        builder.Property(m => m.OccurredAt).IsRequired();
        builder.Property(m => m.ProcessedAt);
        builder.Property(m => m.Attempts).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);
        builder.Property(m => m.LockedUntil);

        // Genuine partial index — only pending rows are indexed, so the
        // dispatcher's 'WHERE ProcessedAt IS NULL ORDER BY OccurredAt' query
        // walks a tiny tree that stays small even after months of dispatched
        // (and yet-to-be-cleaned) rows pile up.
        builder.HasIndex(m => m.OccurredAt)
            .HasDatabaseName("IX_OutboxMessages_Pending")
            .HasFilter("\"ProcessedAt\" IS NULL");
    }
}
