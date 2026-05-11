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
public class ConcurrencyTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public ConcurrencyTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Two PUTs with the same stale If-Match — exactly one wins")]
    public async Task ConcurrentPut_ExactlyOneWins()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        var initialEtag = create.Headers.ETag?.Tag;

        var productId = created.Items[0].ProductId;

        async Task<HttpResponseMessage> SendPutAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sales/{created.Id}")
            {
                Content = JsonContent.Create(PayloadBuilder.BuildUpdate(productId, quantity: 6))
            };
            req.Headers.TryAddWithoutValidation("If-Match", initialEtag);
            return await _client.SendAsync(req);
        }

        var responses = await Task.WhenAll(SendPutAsync(), SendPutAsync());

        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();
        statuses.Count(s => s == 200).Should().Be(1,
            "exactly one PUT may win the optimistic-concurrency race");
        statuses.Count(s => s == 412 || s == 409).Should().Be(1,
            "the loser must be rejected with PreconditionFailed (If-Match stale) or Conflict (DbUpdateConcurrencyException)");

        // The DB must show the qty=6 update applied (the winner's payload),
        // not both, not neither.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        var item = await ctx.SaleItems.AsNoTracking()
            .SingleAsync(i => i.SaleId == created.Id);
        item.Quantity.Should().Be(6);
    }

    [Fact(DisplayName = "Two POSTs with the same Idempotency-Key — at most one sale persists")]
    public async Task ConcurrentIdempotencyKey_AtMostOneSale()
    {
        var idempotencyKey = Guid.NewGuid().ToString();
        var payload = PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}");
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        async Task<HttpResponseMessage> SendAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Idempotency-Key", idempotencyKey);
            return await _client.SendAsync(req);
        }

        // Fire 5 concurrent attempts to make the race more reliable in CI.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => SendAsync()));

        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();

        // The system must produce at most one sale in the DB regardless of
        // how many requests raced for the key.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        var saleCount = await ctx.Sales.AsNoTracking().CountAsync();
        saleCount.Should().Be(1,
            "the Idempotency-Key middleware + unique SaleNumber index together must guarantee exactly one row " +
            $"(observed statuses: {string.Join(", ", statuses)})");

        // At least one response must be the canonical 201 — the first winner.
        statuses.Should().Contain(201);

        // Every other response must be one of: 201 (cached replay), 409
        // (in-flight lock or unique violation race), or 422 (idempotency
        // body mismatch — should NOT happen since bodies are identical).
        var allowed = new[] { 201, 409 };
        statuses.Should().OnlyContain(s => allowed.Contains(s),
            "the only legal outcomes are the cached 201 replay or a 409 from the inflight lock / DB conflict");
    }

    [Fact(DisplayName = "DELETE and PUT racing on the same sale: exactly one wins, the other gets 404 or 409/412")]
    public async Task ConcurrentDeleteAndPut_ExactlyOneWins()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        var productId = created.Items[0].ProductId;
        var initialEtag = create.Headers.ETag?.Tag;

        async Task<HttpResponseMessage> PutAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sales/{created.Id}")
            {
                Content = JsonContent.Create(PayloadBuilder.BuildUpdate(productId, quantity: 8))
            };
            req.Headers.TryAddWithoutValidation("If-Match", initialEtag);
            return await _client.SendAsync(req);
        }

        async Task<HttpResponseMessage> DeleteAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/sales/{created.Id}");
            req.Headers.TryAddWithoutValidation("If-Match", initialEtag);
            return await _client.SendAsync(req);
        }

        var (put, delete) = (PutAsync(), DeleteAsync());
        var responses = await Task.WhenAll(put, delete);
        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();

        statuses.Count(s => s == 200).Should().Be(1,
            "exactly one of the two operations must succeed against a single sale");
        statuses.Count(s => s is 404 or 409 or 412).Should().Be(1,
            "the loser sees the row gone (404) or a concurrency conflict (409/412)");

        // Final state: either the sale is gone (DELETE won) or it has the
        // PUT's qty=8 (PUT won). Anything else is a torn write.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        var item = await ctx.SaleItems.AsNoTracking()
            .SingleOrDefaultAsync(i => i.SaleId == created.Id);

        if (statuses.Contains(200) && responses[1].StatusCode == HttpStatusCode.OK)
        {
            // DELETE won — no items must remain (cascade).
            item.Should().BeNull("DELETE cascade must remove the row's items");
        }
        else
        {
            // PUT won — exactly one item with the new quantity.
            item.Should().NotBeNull("PUT won so the items must still be there");
            item!.Quantity.Should().Be(8);
        }
    }
}
