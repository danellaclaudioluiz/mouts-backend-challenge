using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Services;

namespace Ambev.DeveloperEvaluation.Domain.ValueObjects;

/// <summary>
/// A line-item quantity bounded by the business rule "max
/// <see cref="SaleItemDiscountPolicy.MaxQuantityPerProduct"/> identical
/// items per product per sale". Constructing a Quantity outside the
/// closed interval [1, MaxQuantityPerProduct] is a domain error — the
/// type itself, not a guard scattered across handlers, refuses values
/// that would violate the cap.
/// </summary>
public readonly record struct Quantity
{
    public int Value { get; }

    private Quantity(int value)
    {
        Value = value;
    }

    public static Quantity From(int value)
    {
        if (value < 1)
            throw new DomainException($"Quantity must be at least 1 (got {value}).");
        if (value > SaleItemDiscountPolicy.MaxQuantityPerProduct)
            throw new DomainException(
                $"Quantity cannot exceed {SaleItemDiscountPolicy.MaxQuantityPerProduct} per product per sale (got {value}).");
        return new Quantity(value);
    }

    public static implicit operator int(Quantity q) => q.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
