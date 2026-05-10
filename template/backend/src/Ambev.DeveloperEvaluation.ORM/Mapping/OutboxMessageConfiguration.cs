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

        // Partial index over not-yet-processed rows ordered by time —
        // the dispatcher's hot query.
        builder.HasIndex(m => new { m.ProcessedAt, m.OccurredAt })
            .HasDatabaseName("IX_OutboxMessages_Pending");
    }
}
