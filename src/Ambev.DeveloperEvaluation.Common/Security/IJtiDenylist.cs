namespace Ambev.DeveloperEvaluation.Common.Security;

/// <summary>
/// Denylist for revoked JWT identifiers (jti claim). Entries expire
/// automatically — TTL equals the remaining lifetime of the access
/// token they revoke, so memory never grows unbounded.
/// </summary>
/// <remarks>
/// Used by the JwtBearer middleware on every authenticated request
/// (<c>OnTokenValidated</c>) and by the refresh-token rotation flow
/// to invalidate the *current* access token when the refresh token
/// is consumed.
/// </remarks>
public interface IJtiDenylist
{
    Task RevokeAsync(string jti, TimeSpan remainingLifetime, CancellationToken cancellationToken = default);

    Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default);
}
