using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Variant of <see cref="SalesApiFactory"/> with the API rate limit cranked
/// down to a handful of requests per minute. Lets <c>RateLimitEndpointTests</c>
/// exercise the throttle without affecting the rest of the suite, which uses
/// the relaxed shared factory.
/// </summary>
public class RateLimitedSalesApiFactory : SalesApiFactory
{
    public const int PermitLimit = 5;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:PermitLimit"] = PermitLimit.ToString(),
                ["RateLimit:WindowSeconds"] = "60"
            });
        });
    }
}
