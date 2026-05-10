using System.Text.Json;
using Ambev.DeveloperEvaluation.Domain.Events;

namespace Ambev.DeveloperEvaluation.ORM.Outbox;

/// <summary>
/// Transactional outbox implementation of <see cref="IDomainEventPublisher"/>.
/// Stages each domain event as an OutboxMessage on the request-scoped DbContext;
/// the next SaveChanges (typically inside the same handler that persists the
/// aggregate) commits the aggregate row and the outbox row in a single
/// transaction. Events are dispatched out-of-band by
/// <see cref="OutboxDispatcherService"/>.
/// </summary>
/// <remarks>
/// Calling code MUST schedule a SaveChanges after PublishAsync (e.g. by calling
/// the repository's create/update method right after publishing). If the
/// caller never saves, the staged messages are discarded along with the rest
/// of the unit of work — which is the correct behavior for a failed transaction.
/// </remarks>
public class OutboxDomainEventPublisher : IDomainEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DefaultContext _context;

    public OutboxDomainEventPublisher(DefaultContext context)
    {
        _context = context;
    }

    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
            OccurredAt = domainEvent.OccurredAt,
            Attempts = 0
        };

        _context.OutboxMessages.Add(message);
        return Task.CompletedTask;
    }
}
