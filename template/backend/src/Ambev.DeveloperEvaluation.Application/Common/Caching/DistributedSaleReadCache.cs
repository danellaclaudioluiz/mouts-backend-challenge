using System.Text.Json;
using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Ambev.DeveloperEvaluation.Application.Common.Caching;

/// <summary>
/// IDistributedCache-backed implementation of <see cref="ISaleReadCache"/>.
/// </summary>
/// <remarks>
/// Cache key is <c>sale:{id}</c>; value is the JSON-serialised SaleDto. TTL
/// is a 60-second safety net — explicit eviction on every write keeps the
/// cache fresh, but if an evict ever fails (e.g. Redis blip) the entry will
/// expire on its own within a minute rather than serve stale data
/// indefinitely.
///
/// Every cache operation is BEST-EFFORT: a failing Redis (connection drop,
/// timeout, deserialisation glitch) must NOT cascade into a 500 on the
/// hot read path or block a write. The cache is a latency optimisation,
/// not a source of truth — when it breaks, the handlers fall back to the
/// DB and the request still succeeds, only slower.
/// </remarks>
public class DistributedSaleReadCache : ISaleReadCache
{
    private const string KeyPrefix = "sale:";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedSaleReadCache> _logger;

    public DistributedSaleReadCache(
        IDistributedCache cache,
        ILogger<DistributedSaleReadCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<SaleDto?> TryGetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        byte[]? bytes;
        try
        {
            bytes = await _cache.GetAsync(KeyOf(id), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Redis dropped / DNS hiccup / RedisTimeoutException. Treat as
            // miss so the handler falls through to the DB. Logging at
            // warning so the SRE sees the cache is degraded, but the API
            // surface stays green.
            _logger.LogWarning(ex,
                "Cache GET failed for sale {SaleId} — falling back to DB", id);
            return null;
        }

        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            return JsonSerializer.Deserialize<SaleDto>(bytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Stale or corrupt entry — drop it; the caller will repopulate.
            _logger.LogWarning(ex,
                "Cache value for sale {SaleId} was un-deserialisable, evicting", id);
            await TryEvictAsync(id, cancellationToken);
            return null;
        }
    }

    public async Task SetAsync(SaleDto sale, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(sale, JsonOptions);
            await _cache.SetAsync(
                KeyOf(sale.Id),
                bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Cache SET failed for sale {SaleId} — next read will repopulate from DB", sale.Id);
        }
    }

    public async Task EvictAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(KeyOf(id), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The TTL is the safety net: even if this miss leaves a stale
            // entry, it expires inside 60s. Failing the write here would
            // turn a Redis blip into 500s on every mutation — strictly
            // worse than serving briefly-stale reads.
            _logger.LogWarning(ex,
                "Cache EVICT failed for sale {SaleId} — entry will expire via TTL", id);
        }
    }

    private Task TryEvictAsync(Guid id, CancellationToken cancellationToken) =>
        EvictAsync(id, cancellationToken);

    private static string KeyOf(Guid id) => KeyPrefix + id;
}
