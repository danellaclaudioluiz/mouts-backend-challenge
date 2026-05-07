using System.Text.Json;
using Ambev.DeveloperEvaluation.Domain.Events;
using Microsoft.Extensions.Logging;

namespace Ambev.DeveloperEvaluation.Application.Common.Events;

/// <summary>
/// Default <see cref="IDomainEventPublisher"/> for the challenge: logs each
/// domain event as a structured entry. In production this seam would forward
/// the event to a real message broker (Kafka, RabbitMQ, etc.) without any
/// other code changes.
/// </summary>
public class LoggingDomainEventPublisher : IDomainEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<LoggingDomainEventPublisher> _logger;

    public LoggingDomainEventPublisher(ILogger<LoggingDomainEventPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var eventName = domainEvent.GetType().Name;
        var payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions);

        _logger.LogInformation(
            "Domain event {EventName} occurred at {OccurredAt:o}: {Payload}",
            eventName, domainEvent.OccurredAt, payload);

        return Task.CompletedTask;
    }
}
