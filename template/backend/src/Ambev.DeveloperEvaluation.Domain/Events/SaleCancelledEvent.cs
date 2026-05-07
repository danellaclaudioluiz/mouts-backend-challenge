namespace Ambev.DeveloperEvaluation.Domain.Events;

/// <summary>
/// Raised when a sale is cancelled in full (soft cancel).
/// </summary>
public sealed record SaleCancelledEvent(
    Guid SaleId,
    string SaleNumber) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
