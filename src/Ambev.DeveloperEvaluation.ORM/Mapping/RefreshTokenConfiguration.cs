using Ambev.DeveloperEvaluation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ambev.DeveloperEvaluation.ORM.Mapping;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()");

        builder.Property(t => t.UserId).HasColumnType("uuid").IsRequired();

        // SHA-256 hex digest is 64 chars.
        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);

        builder.Property(t => t.IssuedAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(t => t.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(t => t.RevokedAt).HasColumnType("timestamp with time zone");

        // Refresh lookup is by hash; without an index every /auth/refresh
        // call would seq-scan the whole table.
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.UserId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
