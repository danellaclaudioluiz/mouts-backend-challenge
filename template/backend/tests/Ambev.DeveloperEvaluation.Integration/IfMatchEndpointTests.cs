using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Locks in the contract of <c>SalesController.ParseIfMatch</c>:
/// <list type="bullet">
///   <item>Missing header → no precondition (caller opted out).</item>
///   <item><c>If-Match: *</c> → no precondition (RFC 9110 "any current
///   representation"). Update must succeed even after the row changed.</item>
///   <item><c>If-Match: ""</c> → no precondition (empty quoted string is
///   treated as absent rather than as a literal etag).</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class IfMatchEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public IfMatchEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "PUT with If-Match: * succeeds even after the row was mutated by someone else")]
    public async Task PutWithStarIfMatch_BypassesPrecondition()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate(saleNumber));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        var productId = created.Items[0].ProductId;

        // Someone else bumps RowVersion (PATCH /cancel).
        (await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sales/{created.Id}")
        {
            Content = JsonContent.Create(PayloadBuilder.BuildUpdate(productId))
        };
        update.Headers.TryAddWithoutValidation("If-Match", "*");

        var response = await _client.SendAsync(update);

        // The sale is cancelled, so we expect 400 (domain rule) — but
        // NOT 412. The precondition must have been bypassed.
        response.StatusCode.Should().NotBe(HttpStatusCode.PreconditionFailed,
            "If-Match: * means 'I don't care about the current version' — the precondition must be skipped");
    }

    [Fact(DisplayName = "DELETE with If-Match: * succeeds without checking the row version")]
    public async Task DeleteWithStarIfMatch_Succeeds()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        // Bump RowVersion via a cancel — the * wildcard should still let
        // DELETE through.
        (await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/sales/{created.Id}");
        delete.Headers.TryAddWithoutValidation("If-Match", "*");

        var response = await _client.SendAsync(delete);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "If-Match: * must bypass the optimistic-concurrency check for DELETE just like for PUT (DELETE returns 204 NoContent on success)");
    }

    [Fact(DisplayName = "PUT with If-Match: \"\" (empty quoted string) is treated as no precondition")]
    public async Task PutWithEmptyQuotedIfMatch_BypassesPrecondition()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/sales",
            PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        var productId = created.Items[0].ProductId;

        // Bump RowVersion so a strict header would 412.
        (await _client.PatchAsync($"/api/v1/sales/{created.Id}/cancel", null)).EnsureSuccessStatusCode();

        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sales/{created.Id}")
        {
            Content = JsonContent.Create(PayloadBuilder.BuildUpdate(productId))
        };
        update.Headers.TryAddWithoutValidation("If-Match", "\"\"");

        var response = await _client.SendAsync(update);
        response.StatusCode.Should().NotBe(HttpStatusCode.PreconditionFailed,
            "an empty quoted If-Match is not a real etag — must be treated as absent, not as a literal mismatch");
    }
}
