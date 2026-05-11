namespace Ambev.DeveloperEvaluation.Domain.Events;

/// <summary>
/// Raised when a new user finishes the self-service signup. Carries
/// IDs + email only — NEVER the full User entity, because the outbox
/// publisher serialises the runtime type via
/// <c>JsonSerializer.SerializeToElement(domainEvent, clrType, …)</c>
/// and we must not leak <c>User.Password</c> (the BCrypt hash) into
/// the OutboxMessages table or the dispatcher's structured log.
/// </summary>
[EventType("user.registered.v1")]
public sealed record UserRegisteredEvent(
    Guid UserId,
    string Email,
    string Username) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
