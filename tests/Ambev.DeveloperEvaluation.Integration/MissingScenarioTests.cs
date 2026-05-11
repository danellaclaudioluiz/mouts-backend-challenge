using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Gap-filling E2E scenarios called out by the QA review:
///   - cancel-already-cancelled is idempotent at the HTTP layer too
///   - PUT with an empty items array → 400 (validator)
///   - PUT can grow the items list, not just diff quantity/price
///   - List ordering by multiple keys, with a tie-breaker
///   - List filters combine with AND semantics
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MissingScenarioTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;
    private readonly OutboxAsserter _outbox;

    public MissingScenarioTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _outbox = new OutboxAsserter(factory);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "PATCH /cancel on an already-cancelled sale returns 200 and emits no extra event")]
    public async Task CancelSale_AlreadyCancelled_IsHttpIdempotent()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var first = await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", null);
        first.EnsureSuccessStatusCode();
        var second = await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "the aggregate's Cancel is idempotent, the HTTP contract follows");

        var rows = await _outbox.ReadAllAsync();
        rows.Count(r => r.EventType == "sale.cancelled.v1")
            .Should().Be(1, "the second cancel must not emit a duplicate event");
    }

    [Fact(DisplayName = "PUT with an empty items array is rejected with 400")]
    public async Task UpdateSale_EmptyItems_Returns400()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var response = await _client.PutAsJsonAsync($"/api/v1/sales/{created.Id}", new
        {
            saleDate = DateTime.UtcNow,
            customerId = created.CustomerId,
            customerName = "Renamed",
            branchId = created.BranchId,
            branchName = "Branch",
            items = Array.Empty<object>()
        });

        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "PUT replaces all items with brand-new product ids (full-replace path)")]
    public async Task UpdateSale_ReplacesAllItems()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var newProduct1 = Guid.NewGuid();
        var newProduct2 = Guid.NewGuid();
        var response = await _client.PutAsJsonAsync($"/api/v1/sales/{created.Id}", new
        {
            saleDate = DateTime.UtcNow,
            customerId = created.CustomerId,
            customerName = "Renamed",
            branchId = created.BranchId,
            branchName = "Branch",
            items = new object[]
            {
                new { productId = newProduct1, productName = "Ale",  quantity = 2, unitPrice = 15m },
                new { productId = newProduct2, productName = "Wine", quantity = 1, unitPrice = 30m }
            }
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"PUT body: {body}");

        var get = await _client.GetFromJsonAsync<EnvelopedSale>($"/api/v1/sales/{created.Id}");
        get!.Data.Items.Should().HaveCount(2,
            "the replace path must DELETE the original item and INSERT the two new ones");
        get.Data.Items.Select(i => i.ProductId).Should().BeEquivalentTo(new[] { newProduct1, newProduct2 });
    }

    [Fact(DisplayName = "GET /sales with _order=totalAmount desc, saleDate asc applies the tie-breaker")]
    public async Task List_MultiKeyOrdering_AppliesTieBreaker()
    {
        // Two sales with the SAME totalAmount but different saleDates so
        // the primary sort produces a tie and the secondary key decides.
        var older = DateTime.UtcNow.AddDays(-5);
        var newer = DateTime.UtcNow.AddDays(-1);

        async Task SeedAsync(string num, DateTime date)
        {
            (await _client.PostAsJsonAsync("/api/v1/sales",
                PayloadBuilder.BuildCreate(num, saleDate: date))).EnsureSuccessStatusCode();
        }

        // Both end up with total = 45 (qty=5, price=10, 10% discount).
        var olderNumber = $"S-OLDER-{Guid.NewGuid():N}";
        var newerNumber = $"S-NEWER-{Guid.NewGuid():N}";
        await SeedAsync(olderNumber, older);
        await SeedAsync(newerNumber, newer);

        var response = await _client.GetFromJsonAsync<EnvelopedList>(
            "/api/v1/sales?_order=totalAmount desc, saleDate asc");

        response!.Data.Should().HaveCount(2);
        // saleDate asc tie-break: older first.
        response.Data[0].SaleNumber.Should().Be(olderNumber,
            "with equal totalAmount the secondary 'saleDate asc' key must put the older row first");
        response.Data[1].SaleNumber.Should().Be(newerNumber);
    }

    [Fact(DisplayName = "GET /sales combines customerId + date range + isCancelled with AND semantics")]
    public async Task List_CombinedFilters_AreAndedTogether()
    {
        var targetCustomer = Guid.NewGuid();
        var otherCustomer = Guid.NewGuid();
        var inRange = DateTime.UtcNow.AddDays(-3);
        var outOfRange = DateTime.UtcNow.AddDays(-30);

        async Task<Guid> SeedAsync(Guid customer, DateTime date, bool cancel)
        {
            var resp = await _client.PostAsJsonAsync("/api/v1/sales",
                PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}", customerId: customer, saleDate: date));
            resp.EnsureSuccessStatusCode();
            var id = (await resp.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data.Id;
            if (cancel)
                (await _client.PatchAsync($"/api/v1/sales/{id}/cancel", null)).EnsureSuccessStatusCode();
            return id;
        }

        // 4 sales — only ONE matches the conjunction (target + in-range + active):
        var match     = await SeedAsync(targetCustomer, inRange,     cancel: false);
        await SeedAsync(targetCustomer, outOfRange,  cancel: false); // wrong date
        await SeedAsync(otherCustomer,  inRange,     cancel: false); // wrong customer
        await SeedAsync(targetCustomer, inRange,     cancel: true);  // wrong isCancelled

        var from = inRange.AddDays(-1).ToString("o");
        var to = inRange.AddDays(1).ToString("o");
        var response = await _client.GetFromJsonAsync<EnvelopedList>(
            $"/api/v1/sales?customerId={targetCustomer}&_minDate={from}&_maxDate={to}&isCancelled=false");

        response!.Data.Should().ContainSingle(
            "exactly one seed satisfies customer=target AND date in range AND isCancelled=false");
        response.Data[0].Id.Should().Be(match);
    }
}
