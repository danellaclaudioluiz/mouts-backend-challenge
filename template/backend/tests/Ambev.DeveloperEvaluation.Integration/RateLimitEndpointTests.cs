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

    [Fact(DisplayName = "API surface returns 429 EXACTLY after PermitLimit requests in the same window")]
    public async Task ApiSurface_OverLimit_Returns429()
    {
        await _factory.ResetDatabaseAsync();

        // Hammer a cheap-but-rate-limited route. Single partition (loopback)
        // so every request burns a token from the same FixedWindowLimiter.
        var hits = new List<HttpStatusCode>();
        for (var i = 0; i < RateLimitedSalesApiFactory.PermitLimit + 3; i++)
        {
            var response = await _client.GetAsync("/api/v1/sales?_page=1&_size=1");
            hits.Add(response.StatusCode);
        }

        // The fixed-window limiter must hand out EXACTLY PermitLimit 200s and
        // then flip every subsequent call to 429. A "≥ 1 × 429" check would
        // pass if the limiter incorrectly burned two tokens per request, or
        // if it gave out one extra permit.
        hits.Take(RateLimitedSalesApiFactory.PermitLimit)
            .Should().AllBeEquivalentTo(HttpStatusCode.OK,
                $"the first {RateLimitedSalesApiFactory.PermitLimit} requests must each spend a permit cleanly");

        hits.Skip(RateLimitedSalesApiFactory.PermitLimit)
            .Should().AllBeEquivalentTo(HttpStatusCode.TooManyRequests,
                $"every request after permit #{RateLimitedSalesApiFactory.PermitLimit} must be rejected with 429 inside the same window " +
                $"(observed: {string.Join(", ", hits.Select(c => (int)c))})");
    }
}
