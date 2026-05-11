using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.ValueObjects;

public class PercentageTests
{
    [Theory(DisplayName = "FromPercent converts integer percent to fractional value")]
    [InlineData(0, "0")]
    [InlineData(10, "0.10")]
    [InlineData(20, "0.20")]
    [InlineData(100, "1.00")]
    public void FromPercent_NormalizesToFraction(int percent, string expectedFraction)
    {
        Percentage.FromPercent(percent).Fraction.Should().Be(decimal.Parse(expectedFraction, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "Negative fractions are rejected")]
    public void From_NegativeFraction_Throws()
    {
        var act = () => Percentage.From(-0.01m);
        act.Should().Throw<DomainException>().WithMessage("*cannot be negative*");
    }

    [Fact(DisplayName = "Fractions above 100% are rejected")]
    public void From_AboveOne_Throws()
    {
        var act = () => Percentage.From(1.01m);
        act.Should().Throw<DomainException>().WithMessage("*cannot exceed 100%*");
    }

    [Fact(DisplayName = "ApplyTo of zero percent is Money.Zero")]
    public void ApplyTo_ZeroPercent_IsZero()
    {
        Percentage.Zero.ApplyTo(Money.From(100m)).Should().Be(Money.Zero);
    }

    [Fact(DisplayName = "ApplyTo rounds to 2 dp AwayFromZero (matches Money normalization)")]
    public void ApplyTo_RoundsTwoDecimals()
    {
        // 10% of 0.09 = 0.009 → 0.01
        Percentage.FromPercent(10).ApplyTo(Money.From(0.09m)).Amount.Should().Be(0.01m);
        // 20% of 333.30 = 66.66 (no rounding)
        Percentage.FromPercent(20).ApplyTo(Money.From(333.30m)).Amount.Should().Be(66.66m);
    }
}
