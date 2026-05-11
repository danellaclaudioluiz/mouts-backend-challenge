namespace Ambev.DeveloperEvaluation.Application.Sales.Common;

/// <summary>
/// Read-through cache for the full SaleDto. Lives in front of the database
/// for GET /api/v1/sales/{id} so cache-hot sales don't round-trip Postgres
/// on every read. Backed by <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
/// so multi-pod deployments share the cache.
/// </summary>
public interface ISaleReadCache
{
    Task<SaleDto?> TryGetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SetAsync(SaleDto sale, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evict the cached entry. Called by every write handler after the
    /// transaction commits so the next read goes to the database and the
    /// cache repopulates with the new state — bounded staleness window is
    /// the duration of the eviction RPC, not the TTL.
    /// </summary>
    Task EvictAsync(Guid id, CancellationToken cancellationToken = default);
}
