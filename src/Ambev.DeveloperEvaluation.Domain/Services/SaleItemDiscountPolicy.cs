using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;

namespace Ambev.DeveloperEvaluation.Domain.Services;

/// <summary>
/// Quantity-based discount tiers for sale items, per the challenge rules:
/// <list type="bullet">
///   <item>1 to 3 items: no discount allowed</item>
///   <item>4 to 9 items: 10% discount</item>
///   <item>10 to 20 items: 20% discount</item>
///   <item>more than 20 items: not allowed</item>
/// </list>
/// Pure, side-effect free — easy to unit test and shared by Create/Update flows.
/// </summary>
public static class SaleItemDiscountPolicy
{
    public const int MaxQuantityPerProduct = 20;
    public const int TenPercentTierMinQuantity = 4;
    public const int TwentyPercentTierMinQuantity = 10;

    /// <summary>
    /// Upper bound on the number of distinct line items a single sale may
    /// carry. Bounds the request payload so a malicious or bug-ridden caller
    /// cannot push a multi-MB JSON body through the API surface — and bounds
    /// the resulting transaction's row count for predictable write latency.
    /// </summary>
    public const int MaxItemsPerSale = 100;

    /// <summary>
    /// Computes the discount amount and the resulting line total for a given
    /// quantity and unit price.
    /// </summary>
    /// <exception cref="DomainException">
    /// Thrown when <paramref name="quantity"/> exceeds the per-product cap of 20
    /// or is below 1, or when <paramref name="unitPrice"/> is non-positive.
    /// </exception>
    public static (decimal Discount, decimal Total) Calculate(int quantity, decimal unitPrice)
    {
        // Preserve the primitive overload's historical error wording —
        // existing handler/test contracts depend on it — and only then
        // delegate to the VO-typed core for the actual algebra.
        if (quantity < 1)
            throw new DomainException("Sale item quantity must be at least 1.");
        if (quantity > MaxQuantityPerProduct)
            throw new DomainException(
                $"Cannot sell more than {MaxQuantityPerProduct} identical items.");
        if (unitPrice <= 0m)
            throw new DomainException("Sale item unit price must be greater than zero.");

        var (discount, total) = Calculate(Quantity.From(quantity), Money.From(unitPrice));
        return (discount.Amount, total.Amount);
    }

    /// <summary>
    /// Value-object-typed overload. Preferred entry point: the input
    /// invariants (qty &gt;= 1, qty &lt;= 20, price &gt;= 0) are
    /// enforced by the VO constructors themselves, so this method only
    /// has to encode the tier formula.
    /// </summary>
    public static (Money Discount, Money Total) Calculate(Quantity quantity, Money unitPrice)
    {
        var rate = DiscountRateFor(quantity);
        var gross = unitPrice * quantity;
        var discount = rate.ApplyTo(gross);
        var total = gross - discount;
        return (discount, total);
    }

    private static Percentage DiscountRateFor(int quantity) => quantity switch
    {
        >= TwentyPercentTierMinQuantity => Percentage.FromPercent(20),
        >= TenPercentTierMinQuantity => Percentage.FromPercent(10),
        _ => Percentage.Zero
    };
}
