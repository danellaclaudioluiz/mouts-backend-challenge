using System.Text.Json;
using Ambev.DeveloperEvaluation.Domain.Events;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Events;

/// <summary>
/// Snapshot tests of the JSON wire shape for every outbox event.
/// Downstream consumers (a Kafka topic in production) deserialise these
/// payloads — a renamed field, dropped field, or capitalisation change
/// silently breaks them. The expected strings here are the CONTRACT;
/// updating one means you have already discussed the rename with
/// downstream owners and bumped the event-alias version (`*.v2`).
///
/// Properties intentionally tested as a single ordered JSON literal so
/// the diff on failure points at the exact byte that drifted.
/// </summary>
public class EventPayloadSnapshotTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly DateTime FrozenOccurredAt = new(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "SaleCreatedEvent wire shape — fields, order, types")]
    public void SaleCreatedEvent_Snapshot()
    {
        var evt = new SaleCreatedEvent(
            SaleId: new Guid("11111111-1111-1111-1111-111111111111"),
            SaleNumber: "S-0001",
            CustomerId: new Guid("22222222-2222-2222-2222-222222222222"),
            BranchId: new Guid("33333333-3333-3333-3333-333333333333"),
            TotalAmount: 45.00m,
            ItemCount: 1)
        { /* OccurredAt is init-only via record default; tested separately */ };

        var json = JsonSerializer.Serialize(evt, JsonOptions);
        // Stamp OccurredAt with a frozen value so the snapshot is stable.
        var stamped = System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"occurredAt\":\"[^\"]+\"",
            $"\"occurredAt\":\"{FrozenOccurredAt:o}\"");

        const string expected =
            "{\"saleId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"saleNumber\":\"S-0001\"," +
            "\"customerId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"branchId\":\"33333333-3333-3333-3333-333333333333\"," +
            "\"totalAmount\":45.00," +
            "\"itemCount\":1," +
            "\"occurredAt\":\"2026-05-10T12:00:00.0000000Z\"}";
        stamped.Should().Be(expected);
    }

    [Fact(DisplayName = "SaleModifiedEvent wire shape")]
    public void SaleModifiedEvent_Snapshot()
    {
        var evt = new SaleModifiedEvent(
            new Guid("11111111-1111-1111-1111-111111111111"),
            "S-0001",
            TotalAmount: 90.00m,
            ItemCount: 2);

        var json = JsonSerializer.Serialize(evt, JsonOptions);
        var stamped = System.Text.RegularExpressions.Regex.Replace(
            json, "\"occurredAt\":\"[^\"]+\"", $"\"occurredAt\":\"{FrozenOccurredAt:o}\"");

        const string expected =
            "{\"saleId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"saleNumber\":\"S-0001\"," +
            "\"totalAmount\":90.00," +
            "\"itemCount\":2," +
            "\"occurredAt\":\"2026-05-10T12:00:00.0000000Z\"}";
        stamped.Should().Be(expected);
    }

    [Fact(DisplayName = "SaleCancelledEvent wire shape")]
    public void SaleCancelledEvent_Snapshot()
    {
        var evt = new SaleCancelledEvent(
            new Guid("11111111-1111-1111-1111-111111111111"),
            "S-0001");

        var json = JsonSerializer.Serialize(evt, JsonOptions);
        var stamped = System.Text.RegularExpressions.Regex.Replace(
            json, "\"occurredAt\":\"[^\"]+\"", $"\"occurredAt\":\"{FrozenOccurredAt:o}\"");

        const string expected =
            "{\"saleId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"saleNumber\":\"S-0001\"," +
            "\"occurredAt\":\"2026-05-10T12:00:00.0000000Z\"}";
        stamped.Should().Be(expected);
    }

    [Fact(DisplayName = "ItemCancelledEvent wire shape")]
    public void ItemCancelledEvent_Snapshot()
    {
        var evt = new ItemCancelledEvent(
            SaleId: new Guid("11111111-1111-1111-1111-111111111111"),
            ItemId: new Guid("44444444-4444-4444-4444-444444444444"),
            ProductId: new Guid("55555555-5555-5555-5555-555555555555"),
            Quantity: 3);

        var json = JsonSerializer.Serialize(evt, JsonOptions);
        var stamped = System.Text.RegularExpressions.Regex.Replace(
            json, "\"occurredAt\":\"[^\"]+\"", $"\"occurredAt\":\"{FrozenOccurredAt:o}\"");

        const string expected =
            "{\"saleId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"itemId\":\"44444444-4444-4444-4444-444444444444\"," +
            "\"productId\":\"55555555-5555-5555-5555-555555555555\"," +
            "\"quantity\":3," +
            "\"occurredAt\":\"2026-05-10T12:00:00.0000000Z\"}";
        stamped.Should().Be(expected);
    }
}
