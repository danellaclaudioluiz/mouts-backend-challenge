using Microsoft.Extensions.Caching.Distributed;

namespace Ambev.DeveloperEvaluation.Common.Security;

/// <summary>
/// <see cref="IJtiDenylist"/> backed by <see cref="IDistributedCache"/>.
/// Production wires this to Redis (so multiple API replicas share the
/// same view); the in-memory implementation is fine for single-node
/// dev/test. The key carries the jti directly so lookup is O(1).
/// </summary>
public sealed class DistributedCacheJtiDenylist : IJtiDenylist
{
    private const string KeyPrefix = "jti-deny:";
    private static readonly byte[] Marker = new byte[] { 1 };

    private readonly IDistributedCache _cache;

    public DistributedCacheJtiDenylist(IDistributedCache cache)
    {
        _cache = cache;
    }

    public Task RevokeAsync(string jti, TimeSpan remainingLifetime, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);
        // Already expired? Nothing to denylist — JwtBearer would reject
        // the token on lifetime check anyway.
        if (remainingLifetime <= TimeSpan.Zero)
            return Task.CompletedTask;

        return _cache.SetAsync(
            KeyPrefix + jti,
            Marker,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remainingLifetime },
            cancellationToken);
    }

    public async Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jti))
            return false;
        var value = await _cache.GetAsync(KeyPrefix + jti, cancellationToken);
        return value is not null;
    }
}
