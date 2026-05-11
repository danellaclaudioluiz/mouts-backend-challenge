namespace Ambev.DeveloperEvaluation.Domain.Events;

/// <summary>
/// Raised when a single item inside a sale is cancelled.
/// </summary>
[EventType("sale.item_cancelled.v1")]
public sealed record ItemCancelledEvent(
    Guid SaleId,
    Guid ItemId,
    Guid ProductId,
    int Quantity) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
