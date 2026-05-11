using Ambev.DeveloperEvaluation.Domain.Common;
using Ambev.DeveloperEvaluation.Domain.Exceptions;

namespace Ambev.DeveloperEvaluation.Domain.Entities;

/// <summary>
/// A long-lived rotation token bound to a single user. Stored as a
/// SHA-256 hash (the raw token never lives in the database), so even a
/// full database dump cannot be used to mint access tokens.
/// </summary>
/// <remarks>
/// Rotation is one-shot: each refresh issues a fresh row and revokes
/// the prior one (<see cref="Revoke"/>). Replaying a previously used
/// token therefore both fails the active-row check and signals
/// compromise — handlers may treat that as a trigger for revoking the
/// whole user's refresh chain.
/// </remarks>
public class RefreshToken : BaseEntity
{
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime IssuedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    /// <summary>EF Core required ctor.</summary>
    private RefreshToken() { }

    public static RefreshToken Issue(Guid userId, string tokenHash, TimeSpan lifetime)
    {
        if (userId == Guid.Empty)
            throw new DomainException("Refresh token must be bound to a user.");
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new DomainException("Refresh token hash is required.");
        if (lifetime <= TimeSpan.Zero)
            throw new DomainException("Refresh token lifetime must be positive.");

        var now = DateTime.UtcNow;
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = tokenHash,
            IssuedAt = now,
            ExpiresAt = now + lifetime
        };
    }

    public bool IsActive(DateTime utcNow) => RevokedAt is null && utcNow < ExpiresAt;

    public void Revoke()
    {
        if (RevokedAt is not null)
            return;
        RevokedAt = DateTime.UtcNow;
    }
}
