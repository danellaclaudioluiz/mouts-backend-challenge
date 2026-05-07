using Ambev.DeveloperEvaluation.Domain.Common;
using Ambev.DeveloperEvaluation.Domain.Services;

namespace Ambev.DeveloperEvaluation.Domain.Entities;

/// <summary>
/// A single line on a Sale. Owned by the <see cref="Sale"/> aggregate root —
/// SaleItem instances must only be created or mutated through Sale's methods,
/// which is why state-changing members are <c>internal</c>.
/// </summary>
/// <remarks>
/// Discount and TotalAmount are stored, not computed, so we can filter and
/// order on them in SQL. They are recomputed every time the quantity or unit
/// price changes via <see cref="SaleItemDiscountPolicy"/>.
/// </remarks>
public class SaleItem : BaseEntity
{
    public Guid SaleId { get; internal set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal Discount { get; private set; }
    public decimal TotalAmount { get; private set; }
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

        Id = Guid.NewGuid();
        ProductId = productId;
        ProductName = productName;
        UnitPrice = unitPrice;
        ChangeQuantity(quantity);
    }

    internal void ChangeQuantity(int quantity)
    {
        Quantity = quantity;
        Recalculate();
    }

    internal void ChangeUnitPrice(decimal unitPrice)
    {
        UnitPrice = unitPrice;
        Recalculate();
    }

    internal void Cancel()
    {
        IsCancelled = true;
    }

    private void Recalculate()
    {
        var (discount, total) = SaleItemDiscountPolicy.Calculate(Quantity, UnitPrice);
        Discount = discount;
        TotalAmount = total;
    }
}
