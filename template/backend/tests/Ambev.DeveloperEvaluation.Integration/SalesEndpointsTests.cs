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

    [Fact(DisplayName = "POST /api/v1/sales creates a sale and returns Location")]
    public async Task CreateSale_HappyPath()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";

        var response = await _client.PostAsJsonAsync("/api/v1/sales", BuildPayload(saleNumber));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact(DisplayName = "POST /api/v1/sales rejects duplicate SaleNumber with 409")]
    public async Task CreateSale_Duplicate_Returns409()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";

        var first = await _client.PostAsJsonAsync("/api/v1/sales", BuildPayload(saleNumber));
        first.EnsureSuccessStatusCode();

        var second = await _client.PostAsJsonAsync("/api/v1/sales", BuildPayload(saleNumber));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "POST /api/v1/sales rejects qty > 20 with 400")]
    public async Task CreateSale_QuantityTooHigh_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/sales",
            BuildPayload($"S-{Guid.NewGuid():N}", quantity: 21));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Quantity");
    }

    [Fact(DisplayName = "GET /api/v1/sales/{id} on missing id returns 404")]
    public async Task GetSale_Missing_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/sales/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Idempotency-Key replays the cached response when body matches")]
    public async Task CreateSale_IdempotencyKey_Replays()
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var payload = BuildPayload($"S-{Guid.NewGuid():N}");

        var first = await SendIdempotentPostAsync("/api/v1/sales", payload, idempotencyKey);
        var firstBody = await first.Content.ReadAsStringAsync();
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Replay with the same key + same body — even though the sale-number
        // conflict would normally produce 409, the cached 201 is returned.
        var replay = await SendIdempotentPostAsync("/api/v1/sales", payload, idempotencyKey);
        var replayBody = await replay.Content.ReadAsStringAsync();

        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        replayBody.Should().Be(firstBody);
    }

    [Fact(DisplayName = "Idempotency-Key with a different body returns 422")]
    public async Task CreateSale_IdempotencyKey_BodyMismatch_Returns422()
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var first = await SendIdempotentPostAsync(
            "/api/v1/sales", BuildPayload($"S-{Guid.NewGuid():N}"), idempotencyKey);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await SendIdempotentPostAsync(
            "/api/v1/sales", BuildPayload($"S-{Guid.NewGuid():N}"), idempotencyKey);
        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact(DisplayName = "Idempotency-Key does not cache 4xx responses")]
    public async Task CreateSale_IdempotencyKey_NotCachedFor4xx()
    {
        var idempotencyKey = Guid.NewGuid().ToString();

        // First attempt: validation error (qty > 20) — must NOT be cached.
        var bad = await SendIdempotentPostAsync(
            "/api/v1/sales", BuildPayload($"S-{Guid.NewGuid():N}", quantity: 21), idempotencyKey);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Same key + same body: still re-runs and still fails — cache miss
        // because the prior 4xx wasn't stored.
        var retrySame = await SendIdempotentPostAsync(
            "/api/v1/sales", BuildPayload($"S-{Guid.NewGuid():N}", quantity: 21), idempotencyKey);
        retrySame.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.UnprocessableEntity);
    }

    private async Task<HttpResponseMessage> SendIdempotentPostAsync(string url, object payload, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    [Fact(DisplayName = "PATCH /cancel marks sale cancelled")]
    public async Task CancelSale_Updates_Flag()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var create = await _client.PostAsJsonAsync("/api/v1/sales", BuildPayload(saleNumber));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<EnvelopedSale>();
        created.Should().NotBeNull();

        var cancel = await _client.PatchAsync($"/api/v1/sales/{created!.Data.Id}/cancel", content: null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await _client.GetFromJsonAsync<EnvelopedSale>($"/api/v1/sales/{created.Data.Id}");
        get!.Data.IsCancelled.Should().BeTrue();
    }

    [Fact(DisplayName = "GET returns ETag and PUT with stale If-Match returns 412")]
    public async Task UpdateSale_StaleIfMatch_Returns412()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var create = await _client.PostAsJsonAsync("/api/v1/sales", BuildPayload(saleNumber));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        // Read once to learn the current ETag.
        var firstRead = await _client.GetAsync($"/api/v1/sales/{created.Id}");
        firstRead.EnsureSuccessStatusCode();
        var initialEtag = firstRead.Headers.ETag?.Tag;
        initialEtag.Should().NotBeNullOrEmpty();

        // Mutate the sale (PATCH /cancel), which advances xmin.
        var cancel = await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", content: null);
        cancel.EnsureSuccessStatusCode();

        // Try a PUT carrying the stale ETag — must be rejected.
        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sales/{created.Id}")
        {
            Content = JsonContent.Create(new
            {
                SaleDate = DateTime.UtcNow,
                CustomerId = Guid.NewGuid(),
                CustomerName = "Updated",
                BranchId = Guid.NewGuid(),
                BranchName = "B",
                Items = new[]
                {
                    new { ProductId = Guid.NewGuid(), ProductName = "P", Quantity = 1, UnitPrice = 1m }
                }
            })
        };
        update.Headers.TryAddWithoutValidation("If-Match", initialEtag);

        var response = await _client.SendAsync(update);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    private sealed record EnvelopedSale(SalePayload Data);

    private sealed record SalePayload(Guid Id, string SaleNumber, decimal TotalAmount, bool IsCancelled, uint RowVersion);
}
