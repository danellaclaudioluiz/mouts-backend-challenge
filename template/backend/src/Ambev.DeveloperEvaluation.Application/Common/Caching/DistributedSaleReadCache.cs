using System.Text.Json;
using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Microsoft.Extensions.Caching.Distributed;

namespace Ambev.DeveloperEvaluation.Application.Common.Caching;

/// <summary>
/// IDistributedCache-backed implementation of <see cref="ISaleReadCache"/>.
/// </summary>
/// <remarks>
/// Cache key is <c>sale:{id}</c>; value is the JSON-serialised SaleDto. TTL
/// is a 60-second safety net — explicit eviction on every write keeps the
/// cache fresh, but if an evict ever fails (e.g. Redis blip) the entry will
/// expire on its own within a minute rather than serve stale data
/// indefinitely. The serialiser uses camelCase to match the JSON the API
/// emits, so the cached payload is the exact bytes the controller returns.
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

    public DistributedSaleReadCache(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<SaleDto?> TryGetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var bytes = await _cache.GetAsync(KeyOf(id), cancellationToken);
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            return JsonSerializer.Deserialize<SaleDto>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            // Stale or corrupt entry — drop it; the caller will repopulate.
            await _cache.RemoveAsync(KeyOf(id), cancellationToken);
            return null;
        }
    }

    public Task SetAsync(SaleDto sale, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sale, JsonOptions);
        return _cache.SetAsync(
            KeyOf(sale.Id),
            bytes,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
            cancellationToken);
    }

    public Task EvictAsync(Guid id, CancellationToken cancellationToken = default) =>
        _cache.RemoveAsync(KeyOf(id), cancellationToken);

    private static string KeyOf(Guid id) => KeyPrefix + id;
}
