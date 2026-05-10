using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using Ambev.DeveloperEvaluation.ORM;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

[Collection(IntegrationCollection.Name)]
public class DeleteSaleEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public DeleteSaleEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "DELETE /api/v1/sales/{id} returns 200 + cascades to SaleItems")]
    public async Task DeleteSale_HappyPath_CascadesItems()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var delete = await _client.DeleteAsync($"/api/v1/sales/{created.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await _client.GetAsync($"/api/v1/sales/{created.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Verify cascade actually removed the items rather than orphaning them.
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        var orphans = await context.SaleItems.AsNoTracking()
            .CountAsync(i => i.SaleId == created.Id);
        orphans.Should().Be(0,
            "the FK cascade configured in SaleConfiguration should drop the items with the sale");
    }

    [Fact(DisplayName = "DELETE /api/v1/sales/{id} on unknown id returns 404 problem details")]
    public async Task DeleteSale_Unknown_Returns404()
    {
        var response = await _client.DeleteAsync($"/api/v1/sales/{Guid.NewGuid()}");
        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "DELETE /api/v1/sales/{id} with stale If-Match returns 412")]
    public async Task DeleteSale_StaleIfMatch_Returns412()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var firstRead = await _client.GetAsync($"/api/v1/sales/{created.Id}");
        var staleEtag = firstRead.Headers.ETag?.Tag;

        // Advance the row version by cancelling once.
        var cancel = await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", null);
        cancel.EnsureSuccessStatusCode();

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/sales/{created.Id}");
        delete.Headers.TryAddWithoutValidation("If-Match", staleEtag);

        var response = await _client.SendAsync(delete);
        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.PreconditionFailed);
    }

    [Fact(DisplayName = "DELETE /api/v1/sales/{id} with current If-Match succeeds")]
    public async Task DeleteSale_CurrentIfMatch_Succeeds()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var read = await _client.GetAsync($"/api/v1/sales/{created.Id}");
        var currentEtag = read.Headers.ETag?.Tag;
        currentEtag.Should().NotBeNullOrEmpty();

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/sales/{created.Id}");
        delete.Headers.TryAddWithoutValidation("If-Match", currentEtag);

        var response = await _client.SendAsync(delete);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
