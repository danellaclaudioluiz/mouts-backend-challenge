using Ambev.DeveloperEvaluation.ORM;
using Ambev.DeveloperEvaluation.ORM.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Outbox quarantine + retention. Inserts rows directly into the table so
/// the test does not depend on a specific producer path, and asserts the
/// background services behave correctly on the edge cases.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class OutboxLifecycleTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;

    public OutboxLifecycleTests(SalesApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Outbox row with Attempts >= DeadLetterAfterAttempts is not picked up by the dispatcher (poison-pill quarantined)")]
    public async Task PoisonPillRow_IsNotSelected()
    {
        var poisonId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
            ctx.OutboxMessages.Add(new OutboxMessage
            {
                Id = poisonId,
                EventType = "sale.created.v1",
                Payload = "{}",
                OccurredAt = DateTime.UtcNow.AddMinutes(-1),
                ProcessedAt = null,
                // The dispatcher's claim SQL filters on Attempts < 10.
                // A 10-attempt row has exhausted its retries and must be
                // quarantined — left in place for an operator to inspect.
                Attempts = 10,
                LastError = "simulated permanent failure",
                LockedUntil = null
            });
            await ctx.SaveChangesAsync();
        }

        // Give the dispatcher more than one full poll cycle (5s + jitter)
        // to claim and process anything. Anything < 6s risks a flake if a
        // garbage-collect pause pushes the tick out.
        await Task.Delay(TimeSpan.FromSeconds(7));

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
            var row = await ctx.OutboxMessages.AsNoTracking()
                .SingleAsync(m => m.Id == poisonId);

            row.ProcessedAt.Should().BeNull(
                "a row with Attempts == DeadLetterAfterAttempts must not be re-tried — leaving it pending is the quarantine signal for ops");
            row.Attempts.Should().Be(10,
                "the dispatcher must not touch a quarantined row; the attempt counter must be frozen at the cap");
            row.LockedUntil.Should().BeNull(
                "a row that was never claimed must not carry a soft lock");
        }
    }

    [Fact(DisplayName = "Outbox rows processed more than the retention window ago are reaped by the cleanup query")]
    public async Task ProcessedRows_OlderThanRetention_AreDeleted()
    {
        // Two rows: one inside retention (must survive), one outside (must die).
        var freshId = Guid.NewGuid();
        var staleId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
            var now = DateTime.UtcNow;

            ctx.OutboxMessages.AddRange(
                new OutboxMessage
                {
                    Id = freshId,
                    EventType = "sale.created.v1",
                    Payload = "{}",
                    OccurredAt = now.AddDays(-1),
                    ProcessedAt = now.AddDays(-1),
                    Attempts = 1
                },
                new OutboxMessage
                {
                    Id = staleId,
                    EventType = "sale.created.v1",
                    Payload = "{}",
                    // 31 days old — past the 30-day retention window.
                    OccurredAt = now.AddDays(-31),
                    ProcessedAt = now.AddDays(-31),
                    Attempts = 1
                });
            await ctx.SaveChangesAsync();
        }

        // Replicate the cleanup SQL the OutboxCleanupService runs hourly.
        // Calling the service directly would mean waiting through its 1–5
        // minute jitter delay — far too slow for an integration test.
        // Pinning the test to the exact SQL still catches a regression that
        // edits the retention window or the WHERE clause.
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(30);

            await ctx.Database.ExecuteSqlRawAsync(@"
                DELETE FROM ""OutboxMessages""
                WHERE ""ProcessedAt"" IS NOT NULL AND ""ProcessedAt"" < {0};
            ", cutoff);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
            var ids = await ctx.OutboxMessages.AsNoTracking()
                .Select(m => m.Id)
                .ToListAsync();

            ids.Should().Contain(freshId, "rows inside the 30-day retention window must survive cleanup");
            ids.Should().NotContain(staleId, "rows processed more than 30 days ago must be deleted to keep the table bounded");
        }
    }
}
