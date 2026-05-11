using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Services;
using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Services;

/// <summary>
/// Property-based assertions for <see cref="SaleItemDiscountPolicy"/>.
/// Where the [Theory]-based tests in <c>SaleItemDiscountPolicyTests</c>
/// pin specific tier-border cases, these properties exercise the rule
/// across hundreds of random combinations on every run — catching the
/// classes of bugs that example-based tests miss (off-by-one tier slips,
/// rounding drift, sign flips).
/// </summary>
public class SaleItemDiscountPolicyPropertyTests
{
    // FsCheck generators bounded to the domain's legal range. Money is
    // capped at 1M / unit to avoid decimal overflow during multiplication
    // (qty up to 20, total fits in numeric(18,2)).
    private static Arbitrary<int> ValidQuantity() =>
        Gen.Choose(1, SaleItemDiscountPolicy.MaxQuantityPerProduct).ToArbitrary();

    private static Arbitrary<decimal> ValidUnitPrice() =>
        Gen.Choose(1, 1_000_000)
            .Select(cents => Math.Round((decimal)cents / 100m, 2, MidpointRounding.AwayFromZero))
            .ToArbitrary();

    [Property(DisplayName = "Total = qty × unitPrice − discount, exact 2 dp")]
    public Property TotalEqualsGrossMinusDiscount()
    {
        return Prop.ForAll(ValidQuantity(), ValidUnitPrice(), (qty, unitPrice) =>
        {
            var (discount, total) = SaleItemDiscountPolicy.Calculate(qty, unitPrice);
            var gross = qty * unitPrice;
            return total == gross - discount;
        });
    }

    [Property(DisplayName = "Discount never exceeds the gross amount")]
    public Property DiscountNeverExceedsGross()
    {
        return Prop.ForAll(ValidQuantity(), ValidUnitPrice(), (qty, unitPrice) =>
        {
            var (discount, _) = SaleItemDiscountPolicy.Calculate(qty, unitPrice);
            return discount >= 0m && discount <= qty * unitPrice;
        });
    }

    [Property(DisplayName = "Tier 1–3 always yields zero discount")]
    public Property Tier1to3HasNoDiscount()
    {
        return Prop.ForAll(
            Gen.Choose(1, SaleItemDiscountPolicy.TenPercentTierMinQuantity - 1).ToArbitrary(),
            ValidUnitPrice(),
            (qty, unitPrice) =>
            {
                var (discount, _) = SaleItemDiscountPolicy.Calculate(qty, unitPrice);
                return discount == 0m;
            });
    }

    [Property(DisplayName = "Tier 4–9 discount is round(gross × 10%, 2, AwayFromZero)")]
    public Property Tier4to9IsTenPercent()
    {
        return Prop.ForAll(
            Gen.Choose(
                SaleItemDiscountPolicy.TenPercentTierMinQuantity,
                SaleItemDiscountPolicy.TwentyPercentTierMinQuantity - 1).ToArbitrary(),
            ValidUnitPrice(),
            (qty, unitPrice) =>
            {
                var (discount, _) = SaleItemDiscountPolicy.Calculate(qty, unitPrice);
                var expected = Math.Round(qty * unitPrice * 0.10m, 2, MidpointRounding.AwayFromZero);
                return discount == expected;
            });
    }

    [Property(DisplayName = "Tier 10–20 discount is round(gross × 20%, 2, AwayFromZero)")]
    public Property Tier10to20IsTwentyPercent()
    {
        return Prop.ForAll(
            Gen.Choose(
                SaleItemDiscountPolicy.TwentyPercentTierMinQuantity,
                SaleItemDiscountPolicy.MaxQuantityPerProduct).ToArbitrary(),
            ValidUnitPrice(),
            (qty, unitPrice) =>
            {
                var (discount, _) = SaleItemDiscountPolicy.Calculate(qty, unitPrice);
                var expected = Math.Round(qty * unitPrice * 0.20m, 2, MidpointRounding.AwayFromZero);
                return discount == expected;
            });
    }

    [Property(DisplayName = "Quantity > MaxQuantityPerProduct always throws DomainException")]
    public Property AboveCapAlwaysThrows()
    {
        return Prop.ForAll(
            Gen.Choose(SaleItemDiscountPolicy.MaxQuantityPerProduct + 1, int.MaxValue).ToArbitrary(),
            ValidUnitPrice(),
            (qty, unitPrice) =>
            {
                try
                {
                    SaleItemDiscountPolicy.Calculate(qty, unitPrice);
                    return false;
                }
                catch (DomainException)
                {
                    return true;
                }
            });
    }
}
