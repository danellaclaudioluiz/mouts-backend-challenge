using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ambev.DeveloperEvaluation.ORM.Outbox;

/// <summary>
/// Polls OutboxMessages for un-dispatched rows and emits them via the
/// application log (the challenge's stand-in for a real broker).
/// </summary>
/// <remarks>
/// Two-phase dispatch keeps row locks short even when the publish call is
/// slow (Kafka/RabbitMQ round-trips):
/// <list type="number">
///   <item>
///     <b>Phase 1 (short transaction).</b> SELECT … FOR UPDATE SKIP LOCKED
///     a batch of pending rows whose LockedUntil has expired or is null,
///     stamp <c>LockedUntil = now() + LockTtl</c> on each, and commit
///     immediately. The Postgres row locks are released by the commit but
///     the soft lock keeps other dispatcher replicas from picking the same
///     rows.
///   </item>
///   <item>
///     <b>Phase 2 (no transaction).</b> Call DeliverAsync for each
///     message. With a real broker this is the slow part (network) and we
///     hold no DB locks while it runs.
///   </item>
///   <item>
///     <b>Phase 3 (short transactions).</b> Mark the successful rows
///     (ProcessedAt = now(), LockedUntil = null, LastError = null,
///     Attempts++) in a single bulk UPDATE; record per-row LastError for
///     failures so they retry on the next tick (until the dead-letter cap).
///   </item>
/// </list>
/// Dead-lettered rows (Attempts &gt;= cap) stop being picked by phase 1.
/// </remarks>
public class OutboxDispatcherService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollJitter = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long a soft lock survives if a dispatcher dies mid-publish.
    /// After this expires another dispatcher will pick the row up.
    /// </summary>
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);

    private const int BatchSize = 50;
    private const int DeadLetterAfterAttempts = 10;

    private static readonly string ClaimPendingSql = $"""
        WITH picked AS (
            SELECT "Id"
            FROM "OutboxMessages"
            WHERE "ProcessedAt" IS NULL
              AND "Attempts" < {DeadLetterAfterAttempts}
              AND ("LockedUntil" IS NULL OR "LockedUntil" < now())
            ORDER BY "OccurredAt"
            LIMIT {BatchSize}
            FOR UPDATE SKIP LOCKED
        )
        UPDATE "OutboxMessages" o
        SET "LockedUntil" = now() + INTERVAL '{(int)LockTtl.TotalSeconds} seconds'
        FROM picked
        WHERE o."Id" = picked."Id"
        RETURNING o.*;
        """;

    private const string NotificationChannel = "outbox_pending";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutboxDispatcherService> _logger;

    public OutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OutboxDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox dispatcher started; LISTEN '{Channel}' + poll fallback every {Interval} ± jitter",
            NotificationChannel, PollInterval);

        // Dedicated LISTEN connection — separate from EF's pool so it can
        // stay open for the lifetime of the service without occupying a
        // pooled DbContext. When the trigger fires NOTIFY outbox_pending,
        // WaitAsync below returns immediately and the loop runs a dispatch
        // tick within milliseconds.
        var connectionString = _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");

        await using var listenConnection = new NpgsqlConnection(connectionString);
        await listenConnection.OpenAsync(stoppingToken);
        await using (var listenCmd = listenConnection.CreateCommand())
        {
            listenCmd.CommandText = $"LISTEN {NotificationChannel};";
            await listenCmd.ExecuteNonQueryAsync(stoppingToken);
        }

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

            // Wait for either a NOTIFY (immediate wake) or the poll-interval
            // timeout (safety net in case the LISTEN connection dropped a
            // notification or the trigger isn't installed).
            try
            {
                var jitterMs = Random.Shared.Next(
                    -(int)PollJitter.TotalMilliseconds,
                    (int)PollJitter.TotalMilliseconds);
                var waitFor = PollInterval + TimeSpan.FromMilliseconds(jitterMs);

                // WaitAsync returns true on notification, false on timeout.
                await listenConnection.WaitAsync(waitFor, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown — exit the loop cleanly.
                return;
            }
        }
    }

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        // Phase 1: claim a batch under a short transaction.
        var claimed = await ClaimBatchAsync(cancellationToken);
        if (claimed.Count == 0) return;

        // Phase 2: publish each message outside any transaction. With a real
        // broker, this is the slow part — we hold zero DB locks here.
        var succeeded = new List<Guid>(claimed.Count);
        var failed = new List<(Guid Id, string Error)>();

        foreach (var message in claimed)
        {
            try
            {
                await DeliverAsync(message, cancellationToken);
                succeeded.Add(message.Id);
            }
            catch (Exception ex)
            {
                var error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                failed.Add((message.Id, error));
                _logger.LogWarning(ex,
                    "Failed to dispatch outbox message {MessageId}", message.Id);
            }
        }

        // Phase 3: short transactions to mark outcomes.
        await PersistOutcomesAsync(succeeded, failed, cancellationToken);
    }

    private async Task<List<OutboxMessage>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var rows = await context.OutboxMessages
            .FromSqlRaw(ClaimPendingSql)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    private async Task PersistOutcomesAsync(
        IReadOnlyCollection<Guid> succeeded,
        IReadOnlyCollection<(Guid Id, string Error)> failed,
        CancellationToken cancellationToken)
    {
        if (succeeded.Count == 0 && failed.Count == 0) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();

        if (succeeded.Count > 0)
        {
            const string markProcessedSql = """
                UPDATE "OutboxMessages"
                SET "ProcessedAt" = now() AT TIME ZONE 'utc',
                    "Attempts" = "Attempts" + 1,
                    "LastError" = NULL,
                    "LockedUntil" = NULL
                WHERE "Id" = ANY({0});
                """;

            await context.Database.ExecuteSqlRawAsync(
                markProcessedSql,
                new object[] { succeeded.ToArray() },
                cancellationToken);
        }

        if (failed.Count > 0)
        {
            const string markFailedSql = """
                UPDATE "OutboxMessages"
                SET "Attempts" = "Attempts" + 1,
                    "LastError" = {1},
                    "LockedUntil" = NULL
                WHERE "Id" = {0};
                """;

            foreach (var (id, error) in failed)
            {
                await context.Database.ExecuteSqlRawAsync(
                    markFailedSql,
                    new object[] { id, error },
                    cancellationToken);
            }
        }
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
}
