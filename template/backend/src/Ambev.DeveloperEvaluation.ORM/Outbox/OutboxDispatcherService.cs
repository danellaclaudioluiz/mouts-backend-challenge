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
/// <remarks>
/// Each poll runs inside a serializable transaction with
/// <c>FOR UPDATE SKIP LOCKED</c> on the SELECT, so multiple dispatcher
/// instances (one per app pod) cooperate without producing duplicates:
/// each batch is locked exclusively by the picking instance, and the
/// other instances skip those rows and grab the next batch.
/// </remarks>
public class OutboxDispatcherService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 50;

    private static readonly string SelectPendingSql = $"""
        SELECT *
        FROM "OutboxMessages"
        WHERE "ProcessedAt" IS NULL
        ORDER BY "OccurredAt"
        LIMIT {BatchSize}
        FOR UPDATE SKIP LOCKED
        """;

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

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var pending = await context.OutboxMessages
            .FromSqlRaw(SelectPendingSql)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        foreach (var message in pending)
        {
            try
            {
                await DeliverAsync(message, cancellationToken);

                // Mark as processed only AFTER a successful delivery —
                // at-least-once semantics. If the publish path throws,
                // the row stays pending and the next poll retries it.
                message.ProcessedAt = DateTime.UtcNow;
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
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Stand-in for the eventual broker call. Throwing here is treated as a
    /// transient failure: the row stays pending, attempt counter increments,
    /// and the next poll retries it.
    /// </summary>
    private Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Domain event {EventType} ({MessageId}) occurred at {OccurredAt:o}: {Payload}",
            message.EventType, message.Id, message.OccurredAt, message.Payload);
        return Task.CompletedTask;
    }
}
