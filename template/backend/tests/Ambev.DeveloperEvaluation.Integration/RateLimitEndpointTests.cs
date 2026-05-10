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

        // Use a unique X-Forwarded-For per test method so the two tests in
        // this class don't share a rate-limit partition — without this the
        // test order would leak permits between them. UseForwardedHeaders
        // is wired in Program.cs and the harness ships requests from
        // loopback, which is trusted by default.
        var hits = new List<HttpStatusCode>();
        for (var i = 0; i < RateLimitedSalesApiFactory.PermitLimit + 3; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sales?_page=1&_size=1");
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.0.0.1");
            var response = await _client.SendAsync(req);
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

    [Fact(DisplayName = "Fixed-window limiter resets after the window elapses — exhausted partition recovers")]
    public async Task ApiSurface_AfterWindowReset_RecoversTo200()
    {
        await _factory.ResetDatabaseAsync();

        // A different X-Forwarded-For from the over-limit test so the two
        // don't share a partition (and so the suite's order can't leak
        // permits across them).
        const string testIp = "10.0.0.2";

        var preWindowHits = new List<HttpStatusCode>();
        for (var i = 0; i < RateLimitedSalesApiFactory.PermitLimit + 1; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sales?_page=1&_size=1");
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", testIp);
            var resp = await _client.SendAsync(req);
            preWindowHits.Add(resp.StatusCode);
        }
        preWindowHits.Last().Should().Be(HttpStatusCode.TooManyRequests,
            "sanity check: the window is exhausted before we wait for it to reset");

        // Wait slightly more than one full window. FixedWindowRateLimiter
        // resets its counter when the wall-clock window rolls over, so
        // (WindowSeconds + 1) is enough to guarantee we're in a fresh
        // window regardless of where we were inside the previous one.
        await Task.Delay(TimeSpan.FromSeconds(RateLimitedSalesApiFactory.WindowSeconds + 1));

        var freshReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sales?_page=1&_size=1");
        freshReq.Headers.TryAddWithoutValidation("X-Forwarded-For", testIp);
        var afterReset = await _client.SendAsync(freshReq);
        afterReset.StatusCode.Should().Be(HttpStatusCode.OK,
            "once the fixed window rolls over the limiter must hand out fresh permits");
    }
}
