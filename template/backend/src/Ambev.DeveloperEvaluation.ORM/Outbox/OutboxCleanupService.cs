using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ambev.DeveloperEvaluation.ORM.Outbox;

/// <summary>
/// Periodically deletes processed outbox rows older than a retention window
/// so the table doesn't grow indefinitely. Runs once at startup (after a
/// jitter delay) and then once per <see cref="RunInterval"/>.
/// </summary>
/// <remarks>
/// Safe to run on every replica: a delete that races with another instance
/// just no-ops on the rows that are already gone. The retention window is
/// chosen long enough for any downstream reconciliation, but short enough
/// to keep the table small.
/// </remarks>
public class OutboxCleanupService : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    // Lower/upper bound (in seconds) for the randomised startup jitter. A
    // fixed 1-minute delay would line every replica up on the same hourly
    // tick when they boot together; randomising spreads cleanup work across
    // a 4-minute window so the I/O burst is decorrelated.
    private const int JitterMinSeconds = 60;
    private const int JitterMaxSeconds = 300;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxCleanupService> _logger;

    public OutboxCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(JitterMinSeconds, JitterMaxSeconds));

        _logger.LogInformation(
            "Outbox cleanup started; deletes processed rows older than {Retention} every {Interval} (initial jitter {Jitter})",
            Retention, RunInterval, jitter);

        try
        {
            await Task.Delay(jitter, stoppingToken);
        }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Outbox cleanup loop failed; retrying after {Interval}", RunInterval);
            }

            try
            {
                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (TaskCanceledException) { }
        }
    }

    /// <summary>
    /// Maximum number of rows deleted per chunk. Bounded chunks keep each
    /// transaction short so the table-level lock doesn't block writes,
    /// autovacuum can keep up, and WAL doesn't explode in a single batch.
    /// </summary>
    private const int ChunkSize = 5000;

    /// <summary>
    /// Hard cap on the loop so a single cleanup tick can't run forever in
    /// pathological backlogs. The next hourly tick picks up the leftover.
    /// </summary>
    private const int MaxChunksPerRun = 200;

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();

        var cutoff = DateTime.UtcNow - Retention;
        var totalDeleted = 0;

        // Chunked DELETE — each round drops at most ChunkSize rows in its own
        // short transaction, so we never hold the table lock long enough to
        // slow autovacuum or balloon WAL.
        for (var i = 0; i < MaxChunksPerRun && !cancellationToken.IsCancellationRequested; i++)
        {
            const string sql = @"
                DELETE FROM ""OutboxMessages""
                WHERE ctid IN (
                    SELECT ctid FROM ""OutboxMessages""
                    WHERE ""ProcessedAt"" IS NOT NULL AND ""ProcessedAt"" < {0}
                    LIMIT {1}
                );";

            var deleted = await context.Database.ExecuteSqlRawAsync(
                sql,
                new object[] { cutoff, ChunkSize },
                cancellationToken);

            if (deleted == 0) break;

            totalDeleted += deleted;
        }

        if (totalDeleted > 0)
            _logger.LogInformation(
                "Outbox cleanup removed {Count} processed rows older than {Cutoff:o}",
                totalDeleted, cutoff);
    }
}
