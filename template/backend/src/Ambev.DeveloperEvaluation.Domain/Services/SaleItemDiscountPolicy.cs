using Ambev.DeveloperEvaluation.Domain.Exceptions;

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
    /// Computes the discount amount and the resulting line total for a given
    /// quantity and unit price.
    /// </summary>
    /// <exception cref="DomainException">
    /// Thrown when <paramref name="quantity"/> exceeds the per-product cap of 20
    /// or is below 1, or when <paramref name="unitPrice"/> is non-positive.
    /// </exception>
    public static (decimal Discount, decimal Total) Calculate(int quantity, decimal unitPrice)
    {
        if (quantity < 1)
            throw new DomainException("Sale item quantity must be at least 1.");

        if (quantity > MaxQuantityPerProduct)
            throw new DomainException(
                $"Cannot sell more than {MaxQuantityPerProduct} identical items.");

        if (unitPrice <= 0m)
            throw new DomainException("Sale item unit price must be greater than zero.");

        var rate = DiscountRateFor(quantity);
        var gross = quantity * unitPrice;
        var discount = decimal.Round(gross * rate, 2, MidpointRounding.AwayFromZero);
        var total = gross - discount;
        return (discount, total);
    }

    private static decimal DiscountRateFor(int quantity) => quantity switch
    {
        >= TwentyPercentTierMinQuantity => 0.20m,
        >= TenPercentTierMinQuantity => 0.10m,
        _ => 0m
    };
}
