using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using System.Security.Cryptography;
using System.Text;
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
///   <item>The body hash is computed over the canonical JSON form (parsed
///         and re-emitted with sorted keys, no whitespace) — semantically
///         identical payloads that differ only in formatting hash to the
///         same value, so '{"a":1}' and '{ "a": 1 }' replay each other.</item>
///   <item>Two requests with the same key arriving simultaneously race for
///         a short distributed lock. The first one runs the pipeline; the
///         second receives 409 Conflict instead of starting a parallel
///         handler that would double-write.</item>
///   <item>Keys longer than <see cref="MaxKeyLength"/> bytes are rejected
///         (400) — an unbounded header would let a caller flood the cache
///         with mega-keys.</item>
/// </list>
/// Backed by <see cref="IDistributedCache"/> (24h TTL). In production this
/// resolves to Redis (configured via the <c>Redis</c> connection string) so
/// the key works across pods and survives restarts; in development it falls
/// back to an in-memory implementation.
/// </remarks>
public class IdempotencyMiddleware
{
    private const string HeaderName = "Idempotency-Key";
    private const int MaxKeyLength = 256;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions ProblemJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly byte[] InflightMarker = "INFLIGHT"u8.ToArray();

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

        var keyValue = keys.ToString();
        if (keyValue.Length > MaxKeyLength)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                "Idempotency-Key too long",
                $"Idempotency-Key must be at most {MaxKeyLength} characters; got {keyValue.Length}.");
            return;
        }

        var bodyHash = await HashCanonicalBodyAsync(context.Request);
        var cacheKey = $"idem:{context.Request.Path}:{keyValue}";
        var lockKey = $"{cacheKey}:lock";

        // Replay path: cached final response wins immediately.
        var cachedBytes = await _cache.GetAsync(cacheKey, context.RequestAborted);
        if (cachedBytes is not null && !IsInflightMarker(cachedBytes))
        {
            var cached = JsonSerializer.Deserialize<CachedResponse>(cachedBytes);
            if (cached is null)
            {
                await _next(context);
                return;
            }

            if (cached.RequestHash != bodyHash)
            {
                await WriteIdempotencyMismatchAsync(context, keyValue);
                return;
            }

            _logger.LogInformation("Replaying cached response for Idempotency-Key {Key}", cacheKey);
            context.Response.StatusCode = cached.StatusCode;
            context.Response.ContentType = cached.ContentType;
            await context.Response.Body.WriteAsync(cached.Body, context.RequestAborted);
            return;
        }

        // Race protection: SET NX on the lock key. The first request to win
        // the bid runs the pipeline; concurrent requests with the same key
        // get 409 instead of executing the handler twice.
        if (!await TryAcquireLockAsync(lockKey, context.RequestAborted))
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                "Idempotency-Key in flight",
                $"Another request with Idempotency-Key '{keyValue}' is being processed. " +
                "Retry in a moment with the same key — the result will be cached and replayable.");
            return;
        }

        try
        {
            await ExecuteAndCacheAsync(context, cacheKey, bodyHash);
        }
        finally
        {
            // Release the lock so subsequent (post-cache) replays can run
            // without colliding. The cached response (if 2xx) becomes the
            // source of truth from now on.
            await _cache.RemoveAsync(lockKey, CancellationToken.None);
        }
    }

    private async Task<bool> TryAcquireLockAsync(string lockKey, CancellationToken cancellationToken)
    {
        // IDistributedCache doesn't expose SET NX directly. Best-effort
        // emulation: read; if present, fail; else set with a TTL. Two
        // simultaneous misses can both succeed — narrow window, mitigated
        // further by the unique index on SaleNumber inside the handler. A
        // Redis-native SETNX would close it entirely; document in README.
        var existing = await _cache.GetAsync(lockKey, cancellationToken);
        if (existing is not null) return false;

        await _cache.SetAsync(
            lockKey,
            InflightMarker,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = LockTtl },
            cancellationToken);
        return true;
    }

    private async Task ExecuteAndCacheAsync(HttpContext context, string cacheKey, string bodyHash)
    {
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

            await originalBody.WriteAsync(body, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    /// <summary>
    /// Canonicalises the request body before hashing — two semantically
    /// equal JSON documents that only differ in whitespace or key order
    /// produce the same hash and replay each other.
    /// </summary>
    private static async Task<string> HashCanonicalBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        byte[] canonical;
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                WriteCanonical(doc.RootElement, writer);
            }
            canonical = ms.ToArray();
        }
        catch (JsonException)
        {
            // Non-JSON body (or malformed) — fall back to the raw bytes.
            request.Body.Position = 0;
            using var raw = new MemoryStream();
            await request.Body.CopyToAsync(raw);
            canonical = raw.ToArray();
        }

        request.Body.Position = 0;
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsInflightMarker(byte[] bytes) =>
        bytes.Length == InflightMarker.Length && bytes.AsSpan().SequenceEqual(InflightMarker);

    private static Task WriteIdempotencyMismatchAsync(HttpContext context, string key) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "Idempotency-Key reuse with different payload",
            $"Idempotency-Key '{key}' was first used with a different request body. " +
            "Use a new key for a different request.");

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = $"https://httpstatuses.io/{statusCode}",
            Detail = detail,
            Instance = context.Request.Path
        };

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsync(JsonSerializer.Serialize(problem, ProblemJson));
    }

    private sealed record CachedResponse(int StatusCode, string ContentType, byte[] Body, string RequestHash);
}
