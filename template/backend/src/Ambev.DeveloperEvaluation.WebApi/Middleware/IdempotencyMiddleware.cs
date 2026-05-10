using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace Ambev.DeveloperEvaluation.WebApi.Middleware;

/// <summary>
/// Honours the <c>Idempotency-Key</c> request header on POST endpoints. The
/// first request with a given key runs normally and the response (status +
/// body + content type) is cached; later requests with the same key return
/// the cached response without invoking the pipeline again.
/// </summary>
/// <remarks>
/// Backed by IMemoryCache for the challenge — fine for a single instance.
/// In production, swap for a distributed cache (Redis) so the key still
/// works after a restart or behind a load balancer. Idempotency-Key is
/// only honoured on POST; PUT/DELETE/PATCH are already idempotent at the
/// HTTP semantics level.
/// </remarks>
public class IdempotencyMiddleware
{
    private const string HeaderName = "Idempotency-Key";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !context.Request.Headers.TryGetValue(HeaderName, out var keys) ||
            string.IsNullOrWhiteSpace(keys.ToString()))
        {
            await _next(context);
            return;
        }

        var key = $"idem:{context.Request.Path}:{keys}";

        if (_cache.TryGetValue<CachedResponse>(key, out var cached) && cached is not null)
        {
            _logger.LogInformation("Replaying cached response for Idempotency-Key {Key}", key);
            context.Response.StatusCode = cached.StatusCode;
            context.Response.ContentType = cached.ContentType;
            await context.Response.Body.WriteAsync(cached.Body);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
            buffer.Position = 0;
            var body = buffer.ToArray();

            // Only cache successful or client-error responses; transient
            // 5xx errors should be retryable, not stuck behind a cache.
            if (context.Response.StatusCode is >= 200 and < 500)
            {
                _cache.Set(key, new CachedResponse(
                    context.Response.StatusCode,
                    context.Response.ContentType ?? "application/json",
                    body), CacheTtl);
            }

            await originalBody.WriteAsync(body);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private sealed record CachedResponse(int StatusCode, string ContentType, byte[] Body);
}
