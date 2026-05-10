using System.Text.Json;
using Ambev.DeveloperEvaluation.ORM;
using Ambev.DeveloperEvaluation.ORM.Outbox;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ambev.DeveloperEvaluation.Integration.Helpers;

/// <summary>
/// Reads OutboxMessages directly from the database so tests can assert
/// 'the side-effect actually got persisted in the same transaction' without
/// waiting on the dispatcher's polling clock.
/// </summary>
public sealed class OutboxAsserter
{
    private readonly SalesApiFactory _factory;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public OutboxAsserter(SalesApiFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ReadAllAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        return await ctx.OutboxMessages
            .AsNoTracking()
            .OrderBy(m => m.OccurredAt)
            .ToListAsync();
    }

    public async Task<OutboxMessage> AssertSingleAsync(string eventTypeAlias)
    {
        var rows = await ReadAllAsync();
        var matches = rows.Where(r => r.EventType == eventTypeAlias).ToList();
        matches.Should().ContainSingle(
            $"exactly one outbox row should carry the '{eventTypeAlias}' alias (actual EventTypes: {string.Join(", ", rows.Select(r => r.EventType))})");
        return matches.Single();
    }

    public async Task<IReadOnlyList<OutboxMessage>> AssertContainsAsync(params string[] eventTypeAliases)
    {
        var rows = await ReadAllAsync();
        foreach (var alias in eventTypeAliases)
            rows.Select(r => r.EventType).Should().Contain(alias);
        return rows;
    }

    public async Task<int> CountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        return await ctx.OutboxMessages.CountAsync();
    }

    /// <summary>
    /// Asserts that exactly one outbox row matches <paramref name="eventTypeAlias"/>
    /// and deserialises its Payload into <typeparamref name="T"/>. Lets a test
    /// move past "the alias is there" and actually check that the SaleId,
    /// TotalAmount, etc. on the wire match what the aggregate produced.
    /// </summary>
    public async Task<T> AssertSinglePayloadAsync<T>(string eventTypeAlias) where T : class
    {
        var row = await AssertSingleAsync(eventTypeAlias);
        var payload = JsonSerializer.Deserialize<T>(row.Payload, PayloadJsonOptions);
        payload.Should().NotBeNull(
            $"the outbox row for '{eventTypeAlias}' must hold a deserialisable {typeof(T).Name} payload (raw: {row.Payload})");
        return payload!;
    }
}
