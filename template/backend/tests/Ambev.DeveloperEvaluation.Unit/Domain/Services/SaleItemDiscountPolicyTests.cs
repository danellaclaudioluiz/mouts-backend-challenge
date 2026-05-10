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
}
