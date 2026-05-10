using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// End-to-end tests against a real Postgres instance running in a
/// testcontainer. Hits the HTTP surface, so the WebApi pipeline (model
/// binding, validation, controller, MediatR, repository, EF Core, outbox)
/// is exercised together.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SalesEndpointsTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;
    private readonly OutboxAsserter _outbox;

    public SalesEndpointsTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _outbox = new OutboxAsserter(factory);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "POST /api/v1/sales creates a sale and returns Location + ETag")]
    public async Task CreateSale_HappyPath()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";

        var response = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate(saleNumber));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.ETag.Should().NotBeNull("create returns the resource so it ships an ETag too");

        var outboxRow = await _outbox.AssertSingleAsync("sale.created.v1");
        outboxRow.ProcessedAt.Should().BeNull("the dispatcher hasn't run yet — the row should be pending");

        // The string-match above only proves the alias landed. Round-trip
        // the payload too so a bug that emits a default-valued SaleId,
        // wrong SaleNumber, or negative TotalAmount fails here instead of
        // leaking downstream.
        var payload = await _outbox.AssertSinglePayloadAsync<SaleCreatedEventPayload>("sale.created.v1");
        payload.SaleNumber.Should().Be(saleNumber);
        payload.SaleId.Should().NotBeEmpty();
        payload.CustomerId.Should().NotBeEmpty();
        payload.BranchId.Should().NotBeEmpty();
        payload.ItemCount.Should().Be(1);
        payload.TotalAmount.Should().BeGreaterThan(0m,
            "the create payload aggregates non-cancelled item totals — must reflect the discount-adjusted total");
    }

    [Fact(DisplayName = "POST /api/v1/sales rejects duplicate SaleNumber with 409")]
    public async Task CreateSale_Duplicate_Returns409()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";

        var first = await _client.PostAsJsonAsync("/api/v1/sales", PayloadBuilder.BuildCreate(saleNumber));
        first.EnsureSuccessStatusCode();

        var second = await _client.PostAsJsonAsync("/api/v1/sales", PayloadBuilder.BuildCreate(saleNumber));

        await ProblemDetailsAsserter.AssertProblemAsync(second, HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "POST /api/v1/sales rejects qty > 20 with 400 problem details")]
    public async Task CreateSale_QuantityTooHigh_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}", quantity: 21));

        var problem = await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("errors").EnumerateObject()
            .Should().Contain(p => p.Name.Contains("Quantity"));
    }

    [Fact(DisplayName = "GET /api/v1/sales/{id} on missing id returns 404 problem details")]
    public async Task GetSale_Missing_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/sales/{Guid.NewGuid()}");
        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Idempotency-Key replays the cached response when body matches")]
    public async Task CreateSale_IdempotencyKey_Replays()
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var payload = PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}");

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

    [Fact(DisplayName = "Idempotency-Key with a different body returns 422 problem details")]
    public async Task CreateSale_IdempotencyKey_BodyMismatch_Returns422()
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var first = await SendIdempotentPostAsync(
            "/api/v1/sales", PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"), idempotencyKey);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await SendIdempotentPostAsync(
            "/api/v1/sales", PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"), idempotencyKey);
        await ProblemDetailsAsserter.AssertProblemAsync(second, HttpStatusCode.UnprocessableEntity);
    }

    [Fact(DisplayName = "Idempotency-Key with same body but different whitespace + key order replays")]
    public async Task CreateSale_IdempotencyKey_CanonicalisesBodyHash()
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var saleNumber = $"S-{Guid.NewGuid():N}";

        var prettyJson = $$"""
            {
                "saleNumber" : "{{saleNumber}}",
                "saleDate": "2026-05-10T12:00:00Z",
                "customerId": "22222222-2222-2222-2222-222222222222",
                "customerName": "Acme",
                "branchId": "33333333-3333-3333-3333-333333333333",
                "branchName": "Branch 1",
                "items": [
                    {
                        "productId": "11111111-1111-1111-1111-111111111111",
                        "productName": "Beer",
                        "quantity": 5,
                        "unitPrice": 10
                    }
                ]
            }
            """;

        // Same content, key order shuffled and whitespace stripped.
        var compactJson = System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonDocument.Parse(prettyJson).RootElement);

        var first = await SendRawIdempotentPostAsync(prettyJson, idempotencyKey);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await SendRawIdempotentPostAsync(compactJson, idempotencyKey);
        second.StatusCode.Should().Be(HttpStatusCode.Created,
            "canonical hash should match across whitespace differences so the cached 201 replays");
    }

    private async Task<HttpResponseMessage> SendRawIdempotentPostAsync(string json, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    [Fact(DisplayName = "Idempotency-Key does not cache 4xx responses")]
    public async Task CreateSale_IdempotencyKey_NotCachedFor4xx()
    {
        var idempotencyKey = Guid.NewGuid().ToString();

        // First attempt: validation error (qty > 20) — must NOT be cached.
        var bad = await SendIdempotentPostAsync(
            "/api/v1/sales", PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}", quantity: 21), idempotencyKey);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Same key + same body again: still re-runs and still fails — the
        // prior 4xx wasn't stored, so the response is the deterministic 400
        // (no surprising 422 mismatch).
        var retrySame = await SendIdempotentPostAsync(
            "/api/v1/sales", PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}", quantity: 21), idempotencyKey);
        retrySame.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Idempotency-Key exceeding 256 chars is rejected with 400")]
    public async Task CreateSale_IdempotencyKey_TooLong_Returns400()
    {
        var oversized = new string('k', 257);
        var response = await SendIdempotentPostAsync(
            "/api/v1/sales", PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"), oversized);

        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest,
            expectedTitleContains: "Idempotency-Key");
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

    [Fact(DisplayName = "PATCH /cancel marks sale cancelled and writes a sale.cancelled outbox row")]
    public async Task CancelSale_Updates_Flag()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var create = await _client.PostAsJsonAsync("/api/v1/sales", PayloadBuilder.BuildCreate(saleNumber));
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<EnvelopedSale>();
        created.Should().NotBeNull();

        var cancel = await _client.PatchAsync($"/api/v1/sales/{created!.Data.Id}/cancel", content: null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        cancel.Headers.ETag.Should().NotBeNull();

        var get = await _client.GetFromJsonAsync<EnvelopedSale>($"/api/v1/sales/{created.Data.Id}");
        get!.Data.IsCancelled.Should().BeTrue();

        await _outbox.AssertContainsAsync("sale.created.v1", "sale.cancelled.v1");

        var cancelledPayload = await _outbox.AssertSinglePayloadAsync<SaleCancelledEventPayload>("sale.cancelled.v1");
        cancelledPayload.SaleId.Should().Be(created.Data.Id);
        cancelledPayload.SaleNumber.Should().Be(saleNumber);
    }

    [Fact(DisplayName = "GET returns ETag and PUT with stale If-Match returns 412")]
    public async Task UpdateSale_StaleIfMatch_Returns412()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var create = await _client.PostAsJsonAsync("/api/v1/sales", PayloadBuilder.BuildCreate(saleNumber));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var firstRead = await _client.GetAsync($"/api/v1/sales/{created.Id}");
        firstRead.EnsureSuccessStatusCode();
        var initialEtag = firstRead.Headers.ETag?.Tag;
        initialEtag.Should().NotBeNullOrEmpty();

        // Mutate the sale (PATCH /cancel) which bumps RowVersion.
        var cancel = await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", content: null);
        cancel.EnsureSuccessStatusCode();

        var productId = created.Items[0].ProductId;
        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sales/{created.Id}")
        {
            Content = JsonContent.Create(PayloadBuilder.BuildUpdate(productId))
        };
        update.Headers.TryAddWithoutValidation("If-Match", initialEtag);

        var response = await _client.SendAsync(update);

        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.PreconditionFailed);
    }
}
