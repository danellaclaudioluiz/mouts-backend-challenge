namespace Ambev.DeveloperEvaluation.Domain.Events;

/// <summary>
/// Dispatches domain events out of the aggregate boundary. The default
/// implementation logs to the application log; in production this seam
/// would forward to a real message broker (Kafka, RabbitMQ, etc.).
/// </summary>
public interface IDomainEventPublisher
{
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
