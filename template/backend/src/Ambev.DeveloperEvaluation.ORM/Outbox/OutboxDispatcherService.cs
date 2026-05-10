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
/// Each poll runs inside a transaction with <c>FOR UPDATE SKIP LOCKED</c>
/// on the SELECT, so multiple dispatcher instances (one per app pod)
/// cooperate without producing duplicates: each batch is locked exclusively
/// by the picking instance, and the other instances skip those rows and
/// grab the next batch.
///
/// The poll interval has a small random jitter (±200 ms) so deploys with N
/// replicas don't synchronise their hits on the database. Messages that
/// have failed <see cref="DeadLetterAfterAttempts"/> times in a row are
/// considered poisoned and skipped by the picker; their row stays in the
/// table for an operator to inspect (LastError is preserved).
/// </remarks>
public class OutboxDispatcherService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollJitter = TimeSpan.FromMilliseconds(200);
    private const int BatchSize = 50;

    /// <summary>
    /// Maximum delivery attempts before the message is treated as a
    /// dead-letter — it stops being picked by the dispatcher but stays in
    /// the table with its last error preserved.
    /// </summary>
    private const int DeadLetterAfterAttempts = 10;

    private static readonly string SelectPendingSql = $"""
        SELECT *
        FROM "OutboxMessages"
        WHERE "ProcessedAt" IS NULL
          AND "Attempts" < {DeadLetterAfterAttempts}
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
        _logger.LogInformation("Outbox dispatcher started; polling every {Interval} ± jitter", PollInterval);

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
                // ±200 ms jitter so multiple replicas don't queue up on the
                // same hot millisecond.
                var jitterMs = Random.Shared.Next(
                    -(int)PollJitter.TotalMilliseconds,
                    (int)PollJitter.TotalMilliseconds);
                await Task.Delay(PollInterval + TimeSpan.FromMilliseconds(jitterMs), stoppingToken);
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

        // Capture the DB clock once per batch — the dispatcher uses the
        // server's now() so wall-clock skew between replicas does not
        // pollute the ProcessedAt timeline.
        var batchProcessedAt = await SqlNowAsync(context, cancellationToken);

        var deadLettered = 0;
        foreach (var message in pending)
        {
            try
            {
                await DeliverAsync(message, cancellationToken);

                message.ProcessedAt = batchProcessedAt;
                message.Attempts += 1;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts += 1;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

                if (message.Attempts >= DeadLetterAfterAttempts)
                {
                    deadLettered++;
                    _logger.LogError(ex,
                        "Outbox message {MessageId} hit the dead-letter cap of {Cap} attempts; not retrying",
                        message.Id, DeadLetterAfterAttempts);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "Failed to dispatch outbox message {MessageId} (attempt {Attempts}/{Cap})",
                        message.Id, message.Attempts, DeadLetterAfterAttempts);
                }
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (deadLettered > 0)
            _logger.LogWarning("Outbox dispatcher dead-lettered {Count} message(s) this tick", deadLettered);
    }

    /// <summary>
    /// Stand-in for the eventual broker call. Throwing here is treated as a
    /// transient failure: the row stays pending, attempt counter increments,
    /// and the next poll retries it (until the dead-letter cap).
    /// </summary>
    private Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Domain event {EventType} ({MessageId}) occurred at {OccurredAt:o}: {Payload}",
            message.EventType, message.Id, message.OccurredAt, message.Payload);
        return Task.CompletedTask;
    }

    private static async Task<DateTime> SqlNowAsync(DefaultContext context, CancellationToken cancellationToken)
    {
        var nowList = await context.Database.SqlQueryRaw<DateTime>(
            "SELECT (now() at time zone 'utc') AS \"Value\";").ToListAsync(cancellationToken);
        return nowList[0];
    }
}
