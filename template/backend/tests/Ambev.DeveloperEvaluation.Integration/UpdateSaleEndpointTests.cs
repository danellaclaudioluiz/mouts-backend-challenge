using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

[Collection(IntegrationCollection.Name)]
public class UpdateSaleEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;
    private readonly OutboxAsserter _outbox;

    public UpdateSaleEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _outbox = new OutboxAsserter(factory);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "PUT /api/v1/sales/{id} happy path: returns 200, RowVersion advances, ETag changes")]
    public async Task UpdateSale_HappyPath()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        var firstEtag = create.Headers.ETag?.Tag;

        var productId = created.Items[0].ProductId;
        var update = await _client.PutAsJsonAsync($"/api/v1/sales/{created.Id}",
            PayloadBuilder.BuildUpdate(productId, quantity: 7, unitPrice: 20m, customerName: "Renamed"));
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var newEtag = update.Headers.ETag?.Tag;
        newEtag.Should().NotBeNullOrEmpty();
        newEtag.Should().NotBe(firstEtag, "RowVersion must increment on update");

        var get = await _client.GetFromJsonAsync<EnvelopedSale>($"/api/v1/sales/{created.Id}");
        get!.Data.RowVersion.Should().BeGreaterThan(created.RowVersion);
        get.Data.Items.Should().ContainSingle()
            .Which.Quantity.Should().Be(7);

        await _outbox.AssertContainsAsync("sale.created.v1", "sale.modified.v1");
    }

    [Fact(DisplayName = "PUT /api/v1/sales/{id} preserves item id when only qty changes (diff path)")]
    public async Task UpdateSale_DiffPath_KeepsItemId()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        var originalItem = created.Items[0];

        var update = await _client.PutAsJsonAsync($"/api/v1/sales/{created.Id}",
            PayloadBuilder.BuildUpdate(originalItem.ProductId, quantity: 6));
        update.EnsureSuccessStatusCode();

        var get = await _client.GetFromJsonAsync<EnvelopedSale>($"/api/v1/sales/{created.Id}");
        get!.Data.Items.Should().ContainSingle()
            .Which.Id.Should().Be(originalItem.Id,
                "the diff updates the existing line in place; UpdateItem keeps the id stable");
    }

    [Fact(DisplayName = "PUT /api/v1/sales/{id} on cancelled sale returns 400 domain rule")]
    public async Task UpdateSale_CancelledSale_Returns400()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var cancel = await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", null);
        cancel.EnsureSuccessStatusCode();

        var productId = created.Items[0].ProductId;
        var update = await _client.PutAsJsonAsync($"/api/v1/sales/{created.Id}",
            PayloadBuilder.BuildUpdate(productId));

        var problem = await ProblemDetailsAsserter.AssertProblemAsync(update, HttpStatusCode.BadRequest);
        problem.GetProperty("detail").GetString().Should().Contain("cancelled");
    }

    [Fact(DisplayName = "PUT /api/v1/sales/{id} on unknown id returns 404")]
    public async Task UpdateSale_Unknown_Returns404()
    {
        var update = await _client.PutAsJsonAsync($"/api/v1/sales/{Guid.NewGuid()}",
            PayloadBuilder.BuildUpdate(Guid.NewGuid()));
        await ProblemDetailsAsserter.AssertProblemAsync(update, HttpStatusCode.NotFound);
    }
}
