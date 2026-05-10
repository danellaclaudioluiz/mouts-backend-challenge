using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Services;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Services;

public class SaleItemDiscountPolicyTests
{
    [Theory(DisplayName = "Discount rate matches the documented quantity tier")]
    [InlineData(1, 100, 0, 100)]
    [InlineData(3, 100, 0, 300)]
    [InlineData(4, 100, 40, 360)]
    [InlineData(9, 100, 90, 810)]
    [InlineData(10, 100, 200, 800)]
    [InlineData(15, 100, 300, 1200)]
    [InlineData(20, 100, 400, 1600)]
    public void Calculate_ReturnsExpectedDiscountAndTotal(
        int quantity, decimal unitPrice, decimal expectedDiscount, decimal expectedTotal)
    {
        var (discount, total) = SaleItemDiscountPolicy.Calculate(quantity, unitPrice);

        discount.Should().Be(expectedDiscount);
        total.Should().Be(expectedTotal);
    }

    [Fact(DisplayName = "Quantity above 20 throws DomainException")]
    public void Calculate_AboveCap_Throws()
    {
        var act = () => SaleItemDiscountPolicy.Calculate(21, 10);

        act.Should().Throw<DomainException>()
            .WithMessage("*more than 20 identical items*");
    }

    [Theory(DisplayName = "Non-positive quantity or price is rejected")]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    public void Calculate_InvalidQuantity_Throws(int quantity, decimal unitPrice)
    {
        var act = () => SaleItemDiscountPolicy.Calculate(quantity, unitPrice);
        act.Should().Throw<DomainException>();
    }

    [Theory(DisplayName = "Non-positive unit price is rejected")]
    [InlineData(0)]
    [InlineData(-5)]
    public void Calculate_InvalidUnitPrice_Throws(decimal unitPrice)
    {
        var act = () => SaleItemDiscountPolicy.Calculate(5, unitPrice);
        act.Should().Throw<DomainException>();
    }

    /// <summary>
    /// Border-quantity × awkward-price grid. Catches off-by-one tier slips
    /// (e.g. qty=3 promoted to 10%, qty=10 stuck at 10%) and rounding drift
    /// when the gross has more than 2 fractional digits (33.33 × 4 = 133.32,
    /// 999.99 × 10 = 9999.90, etc.). Discount rounds to 2 dp AwayFromZero.
    /// </summary>
    [Theory(DisplayName = "Tier borders × awkward decimal prices round to 2 dp")]
    // ---- 0% tier (qty 1..3) ----
    [InlineData(1, 0.01, 0, 0.01)]
    [InlineData(3, 0.01, 0, 0.03)]
    [InlineData(1, 33.33, 0, 33.33)]
    [InlineData(3, 999.99, 0, 2999.97)]
    // ---- 10% tier (qty 4..9) ----
    // 4 × 33.33 = 133.32 ; 10% = 13.332 → 13.33 ; total = 119.99
    [InlineData(4, 33.33, 13.33, 119.99)]
    // 9 × 0.01 = 0.09 ; 10% = 0.009 → 0.01 ; total = 0.08
    [InlineData(9, 0.01, 0.01, 0.08)]
    // 4 × 999.99 = 3999.96 ; 10% = 399.996 → 400.00 ; total = 3599.96
    [InlineData(4, 999.99, 400.00, 3599.96)]
    // 9 × 999.99 = 8999.91 ; 10% = 899.991 → 899.99 ; total = 8099.92
    [InlineData(9, 999.99, 899.99, 8099.92)]
    // ---- 20% tier (qty 10..20) ----
    // 10 × 0.01 = 0.10 ; 20% = 0.02 ; total = 0.08
    [InlineData(10, 0.01, 0.02, 0.08)]
    // 20 × 0.01 = 0.20 ; 20% = 0.04 ; total = 0.16
    [InlineData(20, 0.01, 0.04, 0.16)]
    // 10 × 33.33 = 333.30 ; 20% = 66.66 ; total = 266.64
    [InlineData(10, 33.33, 66.66, 266.64)]
    // 20 × 999.99 = 19999.80 ; 20% = 3999.96 ; total = 15999.84
    [InlineData(20, 999.99, 3999.96, 15999.84)]
    public void Calculate_DecimalPrecision_AtTierBorders(
        int quantity, decimal unitPrice, decimal expectedDiscount, decimal expectedTotal)
    {
        var (discount, total) = SaleItemDiscountPolicy.Calculate(quantity, unitPrice);

        discount.Should().Be(expectedDiscount);
        total.Should().Be(expectedTotal);
        // Sanity: the line total equals gross minus rounded discount.
        (quantity * unitPrice - discount).Should().Be(expectedTotal);
    }

    [Theory(DisplayName = "Discount rounds AwayFromZero on .005 ties")]
    // 5 × 0.01 = 0.05 ; 10% = 0.005 → 0.01 (AwayFromZero), not 0.00
    [InlineData(5, 0.01, 0.01, 0.04)]
    public void Calculate_HalfwayRoundsAwayFromZero(
        int quantity, decimal unitPrice, decimal expectedDiscount, decimal expectedTotal)
    {
        var (discount, total) = SaleItemDiscountPolicy.Calculate(quantity, unitPrice);
        discount.Should().Be(expectedDiscount);
        total.Should().Be(expectedTotal);
    }
}
