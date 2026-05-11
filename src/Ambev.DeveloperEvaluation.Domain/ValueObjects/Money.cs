using Ambev.DeveloperEvaluation.Domain.Exceptions;

namespace Ambev.DeveloperEvaluation.Domain.ValueObjects;

/// <summary>
/// A non-negative monetary amount stored at 2-decimal precision —
/// matches the <c>numeric(18,2)</c> column type used across the Sales
/// schema. Construction normalizes the amount with banker-safe
/// <see cref="MidpointRounding.AwayFromZero"/> so the in-memory value
/// always equals what Postgres would round to on persist (eliminates
/// the "12.345 in memory, 12.35 on disk" drift class).
/// </summary>
/// <remarks>
/// Negative amounts throw at construction. The aggregate uses Money
/// for prices, discounts, and totals — every place where the only
/// legal value is &gt;= 0. For deltas that can legitimately be
/// negative, use a signed primitive at the call site and convert.
/// </remarks>
public readonly record struct Money
{
    public decimal Amount { get; }

    public static Money Zero { get; } = new(0m);

    private Money(decimal amount)
    {
        Amount = amount;
    }

    public static Money From(decimal amount)
    {
        if (amount < 0m)
            throw new DomainException($"Money cannot be negative (got {amount}).");
        return new Money(Math.Round(amount, 2, MidpointRounding.AwayFromZero));
    }

    public static Money operator +(Money a, Money b) => From(a.Amount + b.Amount);

    public static Money operator -(Money a, Money b)
    {
        var result = a.Amount - b.Amount;
        if (result < 0m)
            throw new DomainException($"Money subtraction would underflow ({a.Amount} - {b.Amount}).");
        return From(result);
    }

    public static Money operator *(Money m, int multiplier)
    {
        if (multiplier < 0)
            throw new DomainException($"Money cannot be multiplied by a negative integer (got {multiplier}).");
        return From(m.Amount * multiplier);
    }

    public static Money operator *(Money m, Quantity q) => m * q.Value;

    /// <summary>
    /// Safe widening: a <see cref="Money"/> always fits in a <see cref="decimal"/>
    /// (the wrapped Amount). The reverse direction stays explicit via
    /// <see cref="From"/> because constructing Money from an arbitrary
    /// decimal must run the non-negative + 2-dp normalization checks.
    /// </summary>
    public static implicit operator decimal(Money m) => m.Amount;

    public override string ToString() => Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
