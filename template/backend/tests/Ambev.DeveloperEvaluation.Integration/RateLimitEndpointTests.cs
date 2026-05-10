using System.Net;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Exercises the fixed-window API rate limiter. Uses a dedicated
/// <see cref="RateLimitedSalesApiFactory"/> with PermitLimit=5 so the test
/// is fast and the shared integration suite isn't perturbed.
/// </summary>
public class RateLimitEndpointTests : IClassFixture<RateLimitedSalesApiFactory>
{
    private readonly RateLimitedSalesApiFactory _factory;
    private readonly HttpClient _client;

    public RateLimitEndpointTests(RateLimitedSalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact(DisplayName = "API surface returns 429 once the per-partition limit is breached")]
    public async Task ApiSurface_OverLimit_Returns429()
    {
        await _factory.ResetDatabaseAsync();

        // Hammer a cheap-but-rate-limited route. We use GET on the list
        // endpoint with a random page so the response itself is small. All
        // requests come from loopback => same partition => same limiter.
        var hits = new List<HttpStatusCode>();
        for (var i = 0; i < RateLimitedSalesApiFactory.PermitLimit + 5; i++)
        {
            var response = await _client.GetAsync("/api/v1/sales?_page=1&_size=1");
            hits.Add(response.StatusCode);
        }

        hits.Count(c => c == HttpStatusCode.TooManyRequests).Should().BeGreaterThan(0,
            $"after {RateLimitedSalesApiFactory.PermitLimit} permits the fixed-window limiter " +
            $"must start returning 429 (observed: {string.Join(", ", hits.Select(c => (int)c))})");

        // The early calls must succeed — otherwise the API was broken before
        // the limiter even kicked in, and the 429s would be a false positive.
        hits.Take(RateLimitedSalesApiFactory.PermitLimit)
            .Should().Contain(HttpStatusCode.OK);
    }
}
