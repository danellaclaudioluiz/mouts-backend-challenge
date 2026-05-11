using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Services;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.ValueObjects;

public class QuantityTests
{
    [Theory(DisplayName = "Quantity accepts every value in [1, MaxQuantityPerProduct]")]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(20)]
    public void From_WithinRange_Succeeds(int value)
    {
        Quantity.From(value).Value.Should().Be(value);
    }

    [Theory(DisplayName = "Quantity below 1 is rejected")]
    [InlineData(0)]
    [InlineData(-3)]
    public void From_BelowOne_Throws(int value)
    {
        var act = () => Quantity.From(value);
        act.Should().Throw<DomainException>().WithMessage("*at least 1*");
    }

    [Fact(DisplayName = "Quantity above the per-product cap is rejected")]
    public void From_AboveCap_Throws()
    {
        var act = () => Quantity.From(SaleItemDiscountPolicy.MaxQuantityPerProduct + 1);
        act.Should().Throw<DomainException>().WithMessage("*cannot exceed*");
    }

    [Fact(DisplayName = "Implicit conversion to int returns the underlying value")]
    public void ImplicitInt_ReturnsValue()
    {
        int boxed = Quantity.From(7);
        boxed.Should().Be(7);
    }
}
