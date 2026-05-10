using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ambev.DeveloperEvaluation.WebApi.Middleware;

/// <summary>
/// Honours the <c>Idempotency-Key</c> request header on POST endpoints.
/// </summary>
/// <remarks>
/// Behavior matches Stripe's well-known semantics:
/// <list type="bullet">
///   <item>Only successful responses (2xx) are cached. 4xx and 5xx remain
///         retryable with the same key — the next attempt re-runs the
///         pipeline and may produce a different status.</item>
///   <item>The cache entry is keyed by path + header value AND fingerprinted
///         with a SHA-256 hash of the request body. Using the same
///         Idempotency-Key with a different payload returns 422
///         Unprocessable Entity, so a buggy or malicious caller cannot
///         accidentally read the response from someone else's request.</item>
/// </list>
/// Backed by IMemoryCache with a 24-hour TTL — fine for a single instance.
/// In production, swap for IDistributedCache (Redis already in
/// docker-compose) so the key still works after a restart or behind a load
/// balancer.
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

        var bodyHash = await HashRequestBodyAsync(context.Request);
        var cacheKey = $"idem:{context.Request.Path}:{keys}";

        if (_cache.TryGetValue<CachedResponse>(cacheKey, out var cached) && cached is not null)
        {
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
                _cache.Set(cacheKey, new CachedResponse(
                    context.Response.StatusCode,
                    context.Response.ContentType ?? "application/json",
                    body,
                    bodyHash), CacheTtl);
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
