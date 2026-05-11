namespace Ambev.DeveloperEvaluation.Domain.Events;

/// <summary>
/// Marker interface for domain events. Domain events are facts about
/// something that happened in the past inside an aggregate, raised by
/// the aggregate and dispatched after persistence.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// The UTC timestamp when the event occurred.
    /// </summary>
    DateTime OccurredAt { get; }
}
