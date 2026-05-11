namespace Ambev.DeveloperEvaluation.Common.Security;

/// <summary>
/// Mints opaque rotation tokens. Returns the raw token (handed to the
/// client) and a stable hash (persisted server-side). The raw value is
/// never stored — comparing inbound refresh requests goes
/// raw → <see cref="Hash"/> → repo lookup.
/// </summary>
public interface IRefreshTokenGenerator
{
    /// <summary>Cryptographically-random URL-safe token plus its SHA-256 hex digest.</summary>
    (string RawToken, string Hash) Generate();

    /// <summary>Stable hash of a raw token. Same algorithm as <see cref="Generate"/>.</summary>
    string Hash(string rawToken);
}
