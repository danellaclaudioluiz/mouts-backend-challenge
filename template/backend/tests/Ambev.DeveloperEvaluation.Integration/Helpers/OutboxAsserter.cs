using System.Text.Json;
using Ambev.DeveloperEvaluation.ORM;
using FluentAssertions;
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
    /// and deserialises its Payload's <c>data</c> envelope field into
    /// <typeparamref name="T"/>. The producer wraps every event as
    /// <c>{ eventId, eventType, occurredAt, data: { … } }</c> so downstream
    /// consumers have a stable id for deduplication (at-least-once); tests
    /// reach into <c>data</c> to assert the event-specific fields.
    /// </summary>
    public async Task<T> AssertSinglePayloadAsync<T>(string eventTypeAlias) where T : class
    {
        var row = await AssertSingleAsync(eventTypeAlias);
        using var doc = JsonDocument.Parse(row.Payload);

        doc.RootElement.TryGetProperty("eventId", out var eventId).Should().BeTrue(
            $"the envelope for '{eventTypeAlias}' must carry an eventId for consumer deduplication (raw: {row.Payload})");
        eventId.GetGuid().Should().NotBeEmpty();
        doc.RootElement.TryGetProperty("data", out var data).Should().BeTrue(
            $"the envelope for '{eventTypeAlias}' must carry the event under 'data' (raw: {row.Payload})");

        var payload = JsonSerializer.Deserialize<T>(data.GetRawText(), PayloadJsonOptions);
        payload.Should().NotBeNull(
            $"the outbox envelope.data for '{eventTypeAlias}' must hold a deserialisable {typeof(T).Name} payload (raw: {row.Payload})");
        return payload!;
    }
}
