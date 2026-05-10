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

        builder.Property(s => s.SaleNumber).IsRequired().HasMaxLength(50);
        builder.HasIndex(s => s.SaleNumber).IsUnique();

        builder.Property(s => s.SaleDate).IsRequired();

        builder.Property(s => s.CustomerId).IsRequired();
        builder.Property(s => s.CustomerName).IsRequired().HasMaxLength(200);

        builder.Property(s => s.BranchId).IsRequired();
        builder.Property(s => s.BranchName).IsRequired().HasMaxLength(200);

        builder.Property(s => s.TotalAmount).HasPrecision(18, 2);
        builder.Property(s => s.IsCancelled).IsRequired();
        builder.Property(s => s.CreatedAt)
            .IsRequired()
            .HasDefaultValueSql("now() at time zone 'utc'");
        builder.Property(s => s.UpdatedAt);

        // Composite indexes covering the listing patterns observed in the
        // ListSales endpoint: 'sales for customer X by date' and 'sales for
        // branch X by date'. Single-column indexes on CustomerId/BranchId/
        // SaleDate were superseded by these — Postgres can only use one
        // btree per scan and the composite covers both.
        builder.HasIndex(s => new { s.CustomerId, s.SaleDate });
        builder.HasIndex(s => new { s.BranchId, s.SaleDate });

        // Active sales are ~95% of rows and the natural default; partial
        // index keeps the recent-sales-by-date scan tight without bloating
        // the index with cancelled rows.
        builder.HasIndex(s => s.SaleDate)
            .HasDatabaseName("IX_Sales_SaleDate_Active")
            .HasFilter("\"IsCancelled\" = false");

        // Cancelled sales are a small minority — a tiny partial keeps the
        // 'list cancelled by date' scan equally cheap.
        builder.HasIndex(s => s.SaleDate)
            .HasDatabaseName("IX_Sales_SaleDate_Cancelled")
            .HasFilter("\"IsCancelled\" = true");

        // Optimistic concurrency via a bigint maintained by a Postgres
        // BEFORE UPDATE trigger (see migration BigintRowVersionTrigger).
        // EF treats the column as generated-on-add-or-update so it issues
        // the WHERE RowVersion = @oldVersion clause but does not include
        // it in the SET, and reads the new value back via RETURNING. xmin
        // would have done the same job but is reset by VACUUM FREEZE.
        builder.Property(s => s.RowVersion)
            .HasColumnType("bigint")
            .HasDefaultValue(0L)
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

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
