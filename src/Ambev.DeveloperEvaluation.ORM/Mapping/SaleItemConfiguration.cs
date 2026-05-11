using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Services;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainQuantity = Ambev.DeveloperEvaluation.Domain.ValueObjects.Quantity;

namespace Ambev.DeveloperEvaluation.ORM.Mapping;

public class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> builder)
    {
        builder.ToTable("SaleItems", t =>
        {
            // Defence-in-depth: the aggregate already enforces these but the
            // database refuses bad data even if the app is bypassed (raw SQL,
            // future services, accidental backfills).
            t.HasCheckConstraint(
                "CK_SaleItems_Quantity",
                $"\"Quantity\" >= 1 AND \"Quantity\" <= {SaleItemDiscountPolicy.MaxQuantityPerProduct}");
            t.HasCheckConstraint(
                "CK_SaleItems_UnitPrice",
                "\"UnitPrice\" > 0");
            t.HasCheckConstraint(
                "CK_SaleItems_Discount",
                "\"Discount\" >= 0");
            t.HasCheckConstraint(
                "CK_SaleItems_TotalAmount",
                "\"TotalAmount\" >= 0");
        });

        builder.HasKey(i => i.Id);
        // The aggregate assigns Id via Guid.NewGuid() in the SaleItem
        // constructor (DDD identity is the domain's job, not the DB's).
        // Without ValueGeneratedNever, EF Core 8 sees the non-empty Id on
        // a freshly-added child and assumes the entity already exists —
        // every new item then comes out as an UPDATE WHERE Id=… (matching
        // zero rows) instead of an INSERT, and PUT-replace flows fail
        // with a spurious DbUpdateConcurrencyException.
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.SaleId).IsRequired();
        builder.Property(i => i.ProductId).IsRequired();
        builder.Property(i => i.ProductName).IsRequired().HasMaxLength(200);

        // VO ↔ primitive converters keep the storage shape (integer /
        // numeric(18,2)) and the CK_ constraints below identical to the
        // pre-VO migration, while in-memory the aggregate works in
        // domain types. The factories validate on materialisation — a
        // backfill / raw SQL that wrote an out-of-range row would fail
        // load too, not just write.
        builder.Property(i => i.Quantity)
            .HasConversion(v => v.Value, v => DomainQuantity.From(v))
            .IsRequired();
        builder.Property(i => i.UnitPrice)
            .HasConversion(v => v.Amount, v => Money.From(v))
            .HasPrecision(18, 2);
        builder.Property(i => i.Discount)
            .HasConversion(v => v.Amount, v => Money.From(v))
            .HasPrecision(18, 2);
        builder.Property(i => i.TotalAmount)
            .HasConversion(v => v.Amount, v => Money.From(v))
            .HasPrecision(18, 2);
        builder.Property(i => i.IsCancelled).IsRequired();

        // Unique partial index encoding the aggregate's invariant:
        // each ProductId may appear at most once in a sale's non-cancelled
        // items. Postgres enforces it even if a future caller bypasses the
        // domain. Cancelled items are excluded from the constraint so
        // re-adding the same product after a cancel is allowed.
        builder.HasIndex(i => new { i.SaleId, i.ProductId })
            .HasDatabaseName("UX_SaleItems_SaleId_ProductId_Active")
            .IsUnique()
            .HasFilter("\"IsCancelled\" = false");
    }
}
