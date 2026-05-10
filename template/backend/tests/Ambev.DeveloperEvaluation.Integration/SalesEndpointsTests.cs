using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// End-to-end tests against a real Postgres instance running in a
/// testcontainer. Hits the HTTP surface, so the WebApi pipeline (model
/// binding, validation, controller, MediatR, repository, EF Core, outbox)
/// is exercised together.
/// </summary>
public class SalesEndpointsTests : IClassFixture<SalesApiFactory>
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public SalesEndpointsTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static object BuildPayload(string saleNumber, int quantity = 5, decimal unitPrice = 10m) => new
    {
        SaleNumber = saleNumber,
        SaleDate = DateTime.UtcNow,
        CustomerId = Guid.NewGuid(),
        CustomerName = "Acme",
        BranchId = Guid.NewGuid(),
        BranchName = "Branch 1",
        Items = new[]
        {
            new
            {
                ProductId = Guid.NewGuid(),
                ProductName = "Beer",
                Quantity = quantity,
                UnitPrice = unitPrice
            }
        }
    };

    [Fact(DisplayName = "POST /api/sales creates a sale and returns Location")]
    public async Task CreateSale_HappyPath()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";

        var response = await _client.PostAsJsonAsync("/api/sales", BuildPayload(saleNumber));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact(DisplayName = "POST /api/sales rejects duplicate SaleNumber with 409")]
    public async Task CreateSale_Duplicate_Returns409()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";

        var first = await _client.PostAsJsonAsync("/api/sales", BuildPayload(saleNumber));
        first.EnsureSuccessStatusCode();

        var second = await _client.PostAsJsonAsync("/api/sales", BuildPayload(saleNumber));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "POST /api/sales rejects qty > 20 with 400")]
    public async Task CreateSale_QuantityTooHigh_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/sales",
            BuildPayload($"S-{Guid.NewGuid():N}", quantity: 21));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Quantity");
    }

    [Fact(DisplayName = "GET /api/sales/{id} on missing id returns 404")]
    public async Task GetSale_Missing_Returns404()
    {
        var response = await _client.GetAsync($"/api/sales/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Idempotency-Key replays the cached response")]
    public async Task CreateSale_IdempotencyKey_Replays()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var idempotencyKey = Guid.NewGuid().ToString();

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/sales")
        {
            Content = JsonContent.Create(BuildPayload(saleNumber))
        };
        request1.Headers.Add("Idempotency-Key", idempotencyKey);

        var first = await _client.SendAsync(request1);
        var firstBody = await first.Content.ReadAsStringAsync();
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Replay with the same key — even though the sale-number conflict
        // would normally produce 409, the cached 201 response is returned.
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/sales")
        {
            Content = JsonContent.Create(BuildPayload(saleNumber))
        };
        request2.Headers.Add("Idempotency-Key", idempotencyKey);

        var replay = await _client.SendAsync(request2);
        var replayBody = await replay.Content.ReadAsStringAsync();

        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        replayBody.Should().Be(firstBody);
    }

    [Fact(DisplayName = "PATCH /cancel marks sale cancelled")]
    public async Task CancelSale_Updates_Flag()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var create = await _client.PostAsJsonAsync("/api/sales", BuildPayload(saleNumber));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<EnvelopedSale>();
        created.Should().NotBeNull();

        var cancel = await _client.PatchAsync($"/api/sales/{created!.Data.Id}/cancel", content: null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await _client.GetFromJsonAsync<EnvelopedSale>($"/api/sales/{created.Data.Id}");
        get!.Data.IsCancelled.Should().BeTrue();
    }

    private sealed record EnvelopedSale(SalePayload Data);

    private sealed record SalePayload(Guid Id, string SaleNumber, decimal TotalAmount, bool IsCancelled);
}
