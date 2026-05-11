namespace Ambev.DeveloperEvaluation.Domain.Events;

/// <summary>
/// Stable, versioned identifier for a domain event. Decouples the wire
/// representation of the event from the CLR type name, so renaming the
/// class or moving its namespace does not invalidate already-enqueued
/// outbox rows or downstream consumers that route on the alias.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class EventTypeAttribute : Attribute
{
    public EventTypeAttribute(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("Event alias cannot be empty.", nameof(alias));
        Alias = alias;
    }

    /// <summary>
    /// Wire-stable identifier such as <c>"sale.created.v1"</c>.
    /// </summary>
    public string Alias { get; }
}
