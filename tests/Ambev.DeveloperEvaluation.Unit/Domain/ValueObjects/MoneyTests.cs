using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.ValueObjects;

public class MoneyTests
{
    [Fact(DisplayName = "From normalizes to 2 decimal places (AwayFromZero)")]
    public void From_RoundsToTwoDecimalsAwayFromZero()
    {
        Money.From(12.345m).Amount.Should().Be(12.35m);
        Money.From(12.344m).Amount.Should().Be(12.34m);
        Money.From(0.005m).Amount.Should().Be(0.01m);
    }

    [Fact(DisplayName = "From rejects negative amounts")]
    public void From_NegativeAmount_Throws()
    {
        var act = () => Money.From(-0.01m);
        act.Should().Throw<DomainException>().WithMessage("*cannot be negative*");
    }

    [Fact(DisplayName = "Zero is the additive identity")]
    public void Zero_IsAdditiveIdentity()
    {
        var x = Money.From(7.50m);
        (x + Money.Zero).Should().Be(x);
    }

    [Fact(DisplayName = "Addition stays at 2 dp")]
    public void Addition_StaysAtTwoDecimals()
    {
        (Money.From(1.10m) + Money.From(2.20m)).Amount.Should().Be(3.30m);
    }

    [Fact(DisplayName = "Subtraction below zero throws (Money is non-negative)")]
    public void Subtraction_BelowZero_Throws()
    {
        var act = () => Money.From(1m) - Money.From(2m);
        act.Should().Throw<DomainException>().WithMessage("*underflow*");
    }

    [Fact(DisplayName = "Multiplication by Quantity equals integer multiplication")]
    public void Multiplication_ByQuantity_EqualsInt()
    {
        var price = Money.From(9.99m);
        var q = Quantity.From(5);
        (price * q).Should().Be(price * 5);
        (price * q).Amount.Should().Be(49.95m);
    }
}
