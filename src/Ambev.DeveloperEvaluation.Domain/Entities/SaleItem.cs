using Ambev.DeveloperEvaluation.Domain.Common;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Services;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;

namespace Ambev.DeveloperEvaluation.Domain.Entities;

/// <summary>
/// A single line on a Sale. Owned by the <see cref="Sale"/> aggregate root —
/// SaleItem instances must only be created or mutated through Sale's methods,
/// which is why state-changing members are <c>internal</c>.
/// </summary>
/// <remarks>
/// Quantity / UnitPrice / Discount / TotalAmount are value-objects:
/// <see cref="ValueObjects.Quantity"/> + <see cref="ValueObjects.Money"/>.
/// Construction routes through their validating factories so a bad value
/// can't sit in memory waiting for the validator to catch it later — the
/// type itself refuses it. EF Core sees the underlying decimal/int via
/// value converters configured on <see cref="SaleItem"/>'s mapping.
/// </remarks>
public class SaleItem : BaseEntity
{
    public Guid SaleId { get; internal set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public Quantity Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money Discount { get; private set; }
    public Money TotalAmount { get; private set; }
    public bool IsCancelled { get; private set; }

    /// <summary>
    /// Required by EF Core. Do not call from domain code — use
    /// <see cref="Sale.AddItem"/> instead.
    /// </summary>
    private SaleItem()
    {
    }

    internal SaleItem(Guid productId, string productName, int quantity, decimal unitPrice)
    {
        if (productId == Guid.Empty)
            throw new DomainException("Sale item product id is required.");
        if (string.IsNullOrWhiteSpace(productName))
            throw new DomainException("Sale item product name is required.");
        if (unitPrice <= 0m)
            throw new DomainException("Sale item unit price must be greater than zero.");

        Id = Guid.NewGuid();
        ProductId = productId;
        ProductName = productName;
        UnitPrice = Money.From(unitPrice);
        ChangeQuantity(quantity);
    }

    internal void ChangeQuantity(int quantity)
    {
        Quantity = Quantity.From(quantity);
        Recalculate();
    }

    internal void ChangeUnitPrice(decimal unitPrice)
    {
        if (unitPrice <= 0m)
            throw new DomainException("Sale item unit price must be greater than zero.");
        UnitPrice = Money.From(unitPrice);
        Recalculate();
    }

    internal void Replace(string productName, int quantity, decimal unitPrice)
    {
        if (string.IsNullOrWhiteSpace(productName))
            throw new DomainException("Sale item product name is required.");
        if (unitPrice <= 0m)
            throw new DomainException("Sale item unit price must be greater than zero.");

        ProductName = productName;
        UnitPrice = Money.From(unitPrice);
        ChangeQuantity(quantity);
    }

    internal void Cancel()
    {
        IsCancelled = true;
    }

    private void Recalculate()
    {
        // VO-typed overload: invariants (qty range, money non-negative)
        // are already enforced by Quantity/Money themselves at this point.
        var (discount, total) = SaleItemDiscountPolicy.Calculate(Quantity, UnitPrice);
        Discount = discount;
        TotalAmount = total;
    }
}
