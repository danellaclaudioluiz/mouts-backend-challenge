using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ambev.DeveloperEvaluation.ORM.Mapping;

public class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("Sales");

        builder.HasKey(s => s.Id);
        // Aggregate assigns Id via Guid.NewGuid() in Sale.Create() —
        // same rationale as SaleItem.Id (see SaleItemConfiguration).
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.SaleNumber).IsRequired().HasMaxLength(50);
        builder.HasIndex(s => s.SaleNumber).IsUnique();

        builder.Property(s => s.SaleDate).IsRequired();

        builder.Property(s => s.CustomerId).IsRequired();
        builder.Property(s => s.CustomerName).IsRequired().HasMaxLength(200);

        builder.Property(s => s.BranchId).IsRequired();
        builder.Property(s => s.BranchName).IsRequired().HasMaxLength(200);

        // Money is the in-memory type; the column stays numeric(18,2). The
        // converter normalises on materialisation (Money.From rounds to 2 dp
        // AwayFromZero), so any value DB and code agree on a single shape.
        builder.Property(s => s.TotalAmount)
            .HasConversion(v => v.Amount, v => Money.From(v))
            .HasPrecision(18, 2);
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

        // RowVersion is maintained by the BEFORE UPDATE trigger
        // (ambev_sales_bump_rowversion). EF treats it as the concurrency
        // token: it issues the WHERE RowVersion = @oldVersion clause but
        // does NOT include the column in SET (the trigger owns the new
        // value); the post-update value is read back via RETURNING. xmin
        // would have done the same job but is reset by VACUUM FREEZE.
        //
        // Note: this used to false-positive on full-replace PUTs because
        // EF saw non-default Guid keys on freshly-constructed SaleItems
        // and emitted UPDATEs (matching 0 rows) instead of INSERTs.
        // The Id.ValueGeneratedNever() on SaleItemConfiguration fixed
        // that — see commit history for the diagnosis.
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
