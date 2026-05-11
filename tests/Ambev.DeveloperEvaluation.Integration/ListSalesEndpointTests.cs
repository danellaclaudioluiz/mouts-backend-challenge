using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

[Collection(IntegrationCollection.Name)]
public class ListSalesEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public ListSalesEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedSaleAsync(
        string saleNumber,
        Guid? customerId = null,
        Guid? branchId = null,
        DateTime? saleDate = null)
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate(saleNumber, customerId: customerId, branchId: branchId, saleDate: saleDate));
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data.Id;
    }

    [Fact(DisplayName = "GET /api/v1/sales returns paginated list")]
    public async Task List_Pagination()
    {
        for (var i = 0; i < 3; i++)
            await SeedSaleAsync($"S-{Guid.NewGuid():N}");

        var response = await _client.GetFromJsonAsync<EnvelopedList>(
            "/api/v1/sales?_page=1&_size=2");

        response!.Data.Should().HaveCount(2);
        response.TotalCount.Should().Be(3);
        response.TotalPages.Should().Be(2);
        response.NextCursor.Should().NotBeNullOrEmpty(
            "page-1 of a multi-page result now hands back a keyset cursor so a client can transition to cursor mode for the next page without recomputing offsets on a moving dataset");
    }

    [Fact(DisplayName = "GET /api/v1/sales filters by customerId — every returned row belongs to that customer")]
    public async Task List_FilterByCustomer()
    {
        var targetCustomer = Guid.NewGuid();
        await SeedSaleAsync($"S-{Guid.NewGuid():N}", customerId: targetCustomer);
        await SeedSaleAsync($"S-{Guid.NewGuid():N}", customerId: targetCustomer);
        await SeedSaleAsync($"S-{Guid.NewGuid():N}", customerId: Guid.NewGuid());

        var response = await _client.GetFromJsonAsync<EnvelopedList>(
            $"/api/v1/sales?customerId={targetCustomer}");

        response!.Data.Should().HaveCount(2);
        response.Data.Should().OnlyContain(s => s.CustomerId == targetCustomer,
            "the filter must restrict to that customer — a count-only check would pass even if the WHERE clause was dropped");
    }

    [Fact(DisplayName = "GET /api/v1/sales filters by isCancelled")]
    public async Task List_FilterByCancelled()
    {
        var keptId = await SeedSaleAsync($"S-{Guid.NewGuid():N}");
        var cancelledId = await SeedSaleAsync($"S-{Guid.NewGuid():N}");
        (await _client.PatchAsync($"/api/v1/sales/{cancelledId}/cancel", null)).EnsureSuccessStatusCode();

        var onlyCancelled = await _client.GetFromJsonAsync<EnvelopedList>(
            "/api/v1/sales?isCancelled=true");
        onlyCancelled!.Data.Should().ContainSingle()
            .Which.Id.Should().Be(cancelledId);

        var onlyActive = await _client.GetFromJsonAsync<EnvelopedList>(
            "/api/v1/sales?isCancelled=false");
        onlyActive!.Data.Should().ContainSingle()
            .Which.Id.Should().Be(keptId);
    }

    [Fact(DisplayName = "GET /api/v1/sales returns 400 when _order references unknown field")]
    public async Task List_BadOrderField_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/sales?_order=password+desc");
        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "GET /api/v1/sales returns 400 when _size is out of range")]
    public async Task List_BadSize_Returns400()
    {
        var responseZero = await _client.GetAsync("/api/v1/sales?_size=0");
        await ProblemDetailsAsserter.AssertProblemAsync(responseZero, HttpStatusCode.BadRequest);

        var responseTooLarge = await _client.GetAsync("/api/v1/sales?_size=101");
        await ProblemDetailsAsserter.AssertProblemAsync(responseTooLarge, HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "GET /api/v1/sales returns empty page for page beyond range")]
    public async Task List_PageBeyondRange_ReturnsEmpty()
    {
        await SeedSaleAsync($"S-{Guid.NewGuid():N}");

        var response = await _client.GetFromJsonAsync<EnvelopedList>(
            "/api/v1/sales?_page=999&_size=10");

        response!.Data.Should().BeEmpty();
        response.TotalCount.Should().Be(1);
    }

    [Fact(DisplayName = "GET /api/v1/sales keyset cursor walks all pages end-to-end and stops cleanly")]
    public async Task List_CursorMode_WalksAllPages()
    {
        const int total = 5;
        var seeded = new List<Guid>();
        for (var i = 0; i < total; i++)
            seeded.Add(await SeedSaleAsync($"S-{Guid.NewGuid():N}"));

        // First call: page-1 keyset (no _page, no _cursor). The handler
        // returns a NextCursor we then walk forward with.
        var collected = new List<Guid>();
        var firstPage = await _client.GetFromJsonAsync<EnvelopedList>(
            "/api/v1/sales?_size=2");
        firstPage.Should().NotBeNull();
        firstPage!.Data.Should().HaveCount(2,
            "the first page must come back full when there are more rows than the page size");
        firstPage.NextCursor.Should().NotBeNullOrEmpty(
            "the API must hand back a cursor whenever there is a next page in keyset mode");
        collected.AddRange(firstPage.Data.Select(s => s.Id));

        // Walk forward using the cursor until the API stops returning one.
        var cursor = firstPage.NextCursor;
        var safety = 0;
        while (!string.IsNullOrEmpty(cursor))
        {
            safety++.Should().BeLessThan(10, "the walk must terminate — otherwise the cursor loops");

            var next = await _client.GetFromJsonAsync<EnvelopedList>(
                $"/api/v1/sales?_size=2&_cursor={Uri.EscapeDataString(cursor!)}");
            next!.TotalCount.Should().BeNull(
                "cursor mode skips the COUNT(*) — TotalCount must be omitted, not zero-defaulted");

            collected.AddRange(next.Data.Select(s => s.Id));
            cursor = next.NextCursor;

            // The page after the last must come back empty AND with no
            // further cursor.
            if (next.Data.Count == 0)
                next.NextCursor.Should().BeNullOrEmpty("the API must stop emitting cursors once the last row was already returned");
        }

        // Every row was visited exactly once.
        collected.Should().BeEquivalentTo(seeded);
        collected.Should().OnlyHaveUniqueItems("the cursor walk must not yield the same row twice");
    }

    [Fact(DisplayName = "GET /api/v1/sales rejects combining _page and _cursor")]
    public async Task List_PageAndCursorTogether_Returns400()
    {
        var response = await _client.GetAsync(
            "/api/v1/sales?_page=2&_cursor=some-cursor-value");
        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "GET /api/v1/sales with valid _order=totalAmount desc sorts correctly")]
    public async Task List_OrderByTotalAmountDesc()
    {
        // Three sales with distinct totals.
        var p1 = (Guid.NewGuid(), "A", 2, 10m);
        var p2 = (Guid.NewGuid(), "B", 3, 10m);
        var p3 = (Guid.NewGuid(), "C", 4, 10m);

        var c1 = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}", quantity: p1.Item3, unitPrice: p1.Item4));
        c1.EnsureSuccessStatusCode();

        var c2 = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}", quantity: p2.Item3, unitPrice: p2.Item4));
        c2.EnsureSuccessStatusCode();

        var c3 = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}", quantity: p3.Item3, unitPrice: p3.Item4));
        c3.EnsureSuccessStatusCode();

        var response = await _client.GetFromJsonAsync<EnvelopedList>(
            "/api/v1/sales?_order=totalAmount desc");

        response!.Data.Should().BeInDescendingOrder(s => s.TotalAmount);
    }
}
