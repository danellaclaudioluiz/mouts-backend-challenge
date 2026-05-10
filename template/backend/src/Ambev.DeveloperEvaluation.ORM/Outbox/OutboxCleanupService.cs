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
    private static readonly TimeSpan StartupJitter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

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
        _logger.LogInformation(
            "Outbox cleanup started; deletes processed rows older than {Retention} every {Interval}",
            Retention, RunInterval);

        try
        {
            await Task.Delay(StartupJitter, stoppingToken);
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

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();

        var cutoff = DateTime.UtcNow - Retention;
        var deleted = await context.OutboxMessages
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
            _logger.LogInformation(
                "Outbox cleanup removed {Count} processed rows older than {Cutoff:o}",
                deleted, cutoff);
    }
}
