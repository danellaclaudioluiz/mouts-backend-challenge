namespace Ambev.DeveloperEvaluation.ORM.Outbox;

/// <summary>
/// Persistent record of a domain event waiting to be dispatched. Stored in
/// the same database transaction as the aggregate that raised it (transactional
/// outbox), so events never get lost between SaveChanges and the actual publish.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
