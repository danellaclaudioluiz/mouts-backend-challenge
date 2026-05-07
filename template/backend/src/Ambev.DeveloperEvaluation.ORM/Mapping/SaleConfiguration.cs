using Ambev.DeveloperEvaluation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ambev.DeveloperEvaluation.ORM.Mapping;

public class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("Sales");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()");

        builder.Property(s => s.SaleNumber).IsRequired().HasMaxLength(50);
        builder.HasIndex(s => s.SaleNumber).IsUnique();

        builder.Property(s => s.SaleDate).IsRequired();
        builder.HasIndex(s => s.SaleDate);

        builder.Property(s => s.CustomerId).IsRequired();
        builder.HasIndex(s => s.CustomerId);
        builder.Property(s => s.CustomerName).IsRequired().HasMaxLength(200);

        builder.Property(s => s.BranchId).IsRequired();
        builder.HasIndex(s => s.BranchId);
        builder.Property(s => s.BranchName).IsRequired().HasMaxLength(200);

        builder.Property(s => s.TotalAmount).HasPrecision(18, 2);
        builder.Property(s => s.IsCancelled).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt);

        builder.HasMany(s => s.Items)
            .WithOne()
            .HasForeignKey(i => i.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata
            .FindNavigation(nameof(Sale.Items))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(s => s.DomainEvents);
    }
}
