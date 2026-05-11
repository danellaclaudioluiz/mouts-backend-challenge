using Ambev.DeveloperEvaluation.Domain.Exceptions;

namespace Ambev.DeveloperEvaluation.Domain.ValueObjects;

/// <summary>
/// A non-negative percentage in the closed interval [0, 100], stored
/// as a fractional value (e.g. <c>0.20m</c> for "20%"). Use
/// <see cref="ApplyTo"/> to take a fraction of a <see cref="Money"/>
/// amount — keeps rounding in a single place so two callers
/// computing "20% of $9.95" always agree.
/// </summary>
public readonly record struct Percentage
{
    /// <summary>Fractional value: 0.20m for 20%, 1.00m for 100%.</summary>
    public decimal Fraction { get; }

    public static Percentage Zero { get; } = new(0m);

    private Percentage(decimal fraction)
    {
        Fraction = fraction;
    }

    public static Percentage From(decimal fraction)
    {
        if (fraction < 0m)
            throw new DomainException($"Percentage cannot be negative (got {fraction}).");
        if (fraction > 1m)
            throw new DomainException($"Percentage cannot exceed 100% (got {fraction:P0}).");
        return new Percentage(fraction);
    }

    public static Percentage FromPercent(int percent) => From(percent / 100m);

    public Money ApplyTo(Money amount) => Money.From(amount.Amount * Fraction);

    public override string ToString() =>
        Fraction.ToString("P0", System.Globalization.CultureInfo.InvariantCulture);
}
