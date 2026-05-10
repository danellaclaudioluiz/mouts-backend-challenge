using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

[Collection(IntegrationCollection.Name)]
public class CancelSaleItemEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;
    private readonly OutboxAsserter _outbox;

    public CancelSaleItemEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _outbox = new OutboxAsserter(factory);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "PATCH /items/{itemId}/cancel marks item cancelled, recalculates total, writes event")]
    public async Task CancelSaleItem_HappyPath()
    {
        // Create with two items so cancelling one leaves the other contributing to the total.
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var extraProductId = Guid.NewGuid();
        var payload = PayloadBuilder.BuildCreate(
            saleNumber,
            quantity: 5,
            unitPrice: 10m,
            extraItems: new[] { (extraProductId, "Ale", 4, 20m) });

        var create = await _client.PostAsJsonAsync("/api/v1/sales", payload);
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        var totalBefore = created.TotalAmount;

        var itemToCancel = created.Items.First(i => i.ProductId == extraProductId);
        var cancel = await _client.PatchAsync(
            $"/api/v1/sales/{created.Id}/items/{itemToCancel.Id}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        cancel.Headers.ETag.Should().NotBeNull("PATCH returns the resource and emits the new ETag");

        var get = await _client.GetFromJsonAsync<EnvelopedSale>($"/api/v1/sales/{created.Id}");
        get!.Data.IsCancelled.Should().BeFalse("sale-level Cancel is a separate operation");
        get.Data.Items.Should().Contain(i => i.Id == itemToCancel.Id && i.IsCancelled);
        get.Data.TotalAmount.Should().BeLessThan(totalBefore,
            "the cancelled item's contribution must come off the total");
        get.Data.ActiveItemsCount.Should().Be(1,
            "Sale.ActiveItemsCount is denormalised on the aggregate; after one cancel it drops by one");

        await _outbox.AssertContainsAsync("sale.created.v1", "sale.item_cancelled.v1");
    }

    [Fact(DisplayName = "PATCH /items/{itemId}/cancel is idempotent — second call does not emit another event")]
    public async Task CancelSaleItem_Idempotent()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        var itemId = created.Items[0].Id;

        var first = await _client.PatchAsync($"/api/v1/sales/{created.Id}/items/{itemId}/cancel", null);
        first.EnsureSuccessStatusCode();
        var second = await _client.PatchAsync($"/api/v1/sales/{created.Id}/items/{itemId}/cancel", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "the aggregate's CancelItem is a no-op on already-cancelled items");

        // Exactly one sale.item_cancelled.v1 event — the second call MUST NOT
        // emit a duplicate.
        var rows = await _outbox.ReadAllAsync();
        rows.Count(r => r.EventType == "sale.item_cancelled.v1")
            .Should().Be(1);
    }

    [Fact(DisplayName = "PATCH /items/{itemId}/cancel on unknown sale returns 404")]
    public async Task CancelSaleItem_UnknownSale_Returns404()
    {
        var response = await _client.PatchAsync(
            $"/api/v1/sales/{Guid.NewGuid()}/items/{Guid.NewGuid()}/cancel", null);
        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "PATCH /items/{itemId}/cancel on unknown item returns 400 domain rule")]
    public async Task CancelSaleItem_UnknownItem_Returns400()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var response = await _client.PatchAsync(
            $"/api/v1/sales/{created.Id}/items/{Guid.NewGuid()}/cancel", null);

        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }
}
