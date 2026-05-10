using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ambev.DeveloperEvaluation.ORM.Outbox;

/// <summary>
/// Polls OutboxMessages for un-dispatched rows and emits them via the
/// application log (the challenge's stand-in for a real broker). Keeps a
/// per-message attempt counter and last-error string so transient failures
/// stay visible without losing the event.
/// </summary>
public class OutboxDispatcherService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherService> _logger;

    public OutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox dispatcher started; polling every {Interval}", PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Outbox dispatcher loop failed; retrying after {Interval}", PollInterval);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException) { }
        }
    }

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();

        var pending = await context.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var message in pending)
        {
            try
            {
                _logger.LogInformation(
                    "Domain event {EventType} ({MessageId}) occurred at {OccurredAt:o}: {Payload}",
                    message.EventType, message.Id, message.OccurredAt, message.Payload);

                message.ProcessedAt = now;
                message.Attempts += 1;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts += 1;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                _logger.LogWarning(ex,
                    "Failed to dispatch outbox message {MessageId} (attempt {Attempts})",
                    message.Id, message.Attempts);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
