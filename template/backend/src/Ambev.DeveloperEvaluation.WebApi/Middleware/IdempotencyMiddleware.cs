using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ambev.DeveloperEvaluation.WebApi.Middleware;

/// <summary>
/// Honours the <c>Idempotency-Key</c> request header on POST endpoints.
/// </summary>
/// <remarks>
/// Behaviour matches Stripe's well-known semantics:
/// <list type="bullet">
///   <item>Only successful responses (2xx) are cached. 4xx and 5xx remain
///         retryable with the same key — the next attempt re-runs the
///         pipeline and may produce a different status.</item>
///   <item>The cache entry is keyed by path + header value AND fingerprinted
///         with a SHA-256 hash of the request body. Using the same
///         Idempotency-Key with a different payload returns 422
///         Unprocessable Entity.</item>
/// </list>
/// Backed by <see cref="IDistributedCache"/> (24h TTL). In production this
/// resolves to Redis (configured via the <c>Redis</c> connection string) so
/// the key works across pods and survives restarts; in development it falls
/// back to an in-memory implementation.
/// </remarks>
public class IdempotencyMiddleware
{
    private const string HeaderName = "Idempotency-Key";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions ProblemJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RequestDelegate _next;
    private readonly IDistributedCache _cache;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        IDistributedCache cache,
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

        var bodyHash = await HashRequestBodyAsync(context.Request);
        var cacheKey = $"idem:{context.Request.Path}:{keys}";

        var cachedBytes = await _cache.GetAsync(cacheKey, context.RequestAborted);
        if (cachedBytes is not null)
        {
            var cached = JsonSerializer.Deserialize<CachedResponse>(cachedBytes);
            if (cached is null)
            {
                await _next(context);
                return;
            }

            if (cached.RequestHash != bodyHash)
            {
                await WriteIdempotencyMismatchAsync(context, keys.ToString());
                return;
            }

            _logger.LogInformation("Replaying cached response for Idempotency-Key {Key}", cacheKey);
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

            // Cache only 2xx — clients should be able to retry the same
            // Idempotency-Key after fixing a 4xx and get a fresh attempt.
            if (context.Response.StatusCode is >= 200 and < 300)
            {
                var entry = new CachedResponse(
                    context.Response.StatusCode,
                    context.Response.ContentType ?? "application/json",
                    body,
                    bodyHash);

                await _cache.SetAsync(
                    cacheKey,
                    JsonSerializer.SerializeToUtf8Bytes(entry),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl },
                    context.RequestAborted);
            }

            await originalBody.WriteAsync(body);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static async Task<string> HashRequestBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(request.Body);

        request.Body.Position = 0;
        return Convert.ToHexString(hash);
    }

    private static Task WriteIdempotencyMismatchAsync(HttpContext context, string key)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "Idempotency-Key reuse with different payload",
            Type = "https://httpstatuses.io/422",
            Detail = $"Idempotency-Key '{key}' was first used with a different request body. " +
                     "Use a new key for a different request.",
            Instance = context.Request.Path
        };

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = problem.Status.Value;
        return context.Response.WriteAsync(JsonSerializer.Serialize(problem, ProblemJson));
    }

    private sealed record CachedResponse(int StatusCode, string ContentType, byte[] Body, string RequestHash);
}
