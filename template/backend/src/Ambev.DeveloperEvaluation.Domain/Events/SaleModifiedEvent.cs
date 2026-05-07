namespace Ambev.DeveloperEvaluation.Domain.Events;

/// <summary>
/// Raised when an existing sale's header or items are modified
/// (excluding cancellations, which have their own events).
/// </summary>
public sealed record SaleModifiedEvent(
    Guid SaleId,
    string SaleNumber,
    decimal TotalAmount,
    int ItemCount) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
