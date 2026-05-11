using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Ambev.DeveloperEvaluation.Domain.Specifications;
using MediatR;
using Microsoft.Extensions.Configuration;
using DomainRefreshToken = Ambev.DeveloperEvaluation.Domain.Entities.RefreshToken;

namespace Ambev.DeveloperEvaluation.Application.Auth.RefreshToken;

/// <summary>
/// Rotates a refresh token: validates the inbound raw token against
/// its stored hash, revokes the row in place, issues a fresh refresh
/// token + a fresh access JWT, and denylists the caller's current
/// access-token jti so the prior JWT can't keep being used until it
/// naturally expires.
/// </summary>
/// <remarks>
/// Failure modes (revoked, expired, unknown hash, suspended user) all
/// surface as <see cref="UnauthorizedAccessException"/> with the same
/// opaque message — refresh is an auth boundary and discriminating
/// errors would leak whether a given token ever existed.
/// </remarks>
public sealed class RefreshTokenHandler : IRequestHandler<RefreshTokenCommand, RefreshTokenResult>
{
    private const double DefaultRefreshLifetimeHours = 168; // 7 days

    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IRefreshTokenGenerator _refreshTokenGenerator;
    private readonly IUserRepository _userRepository;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IJtiDenylist _jtiDenylist;
    private readonly IConfiguration _configuration;

    public RefreshTokenHandler(
        IRefreshTokenRepository refreshTokenRepository,
        IRefreshTokenGenerator refreshTokenGenerator,
        IUserRepository userRepository,
        IJwtTokenGenerator jwtTokenGenerator,
        IJtiDenylist jtiDenylist,
        IConfiguration configuration)
    {
        _refreshTokenRepository = refreshTokenRepository;
        _refreshTokenGenerator = refreshTokenGenerator;
        _userRepository = userRepository;
        _jwtTokenGenerator = jwtTokenGenerator;
        _jtiDenylist = jtiDenylist;
        _configuration = configuration;
    }

    public async Task<RefreshTokenResult> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var inboundHash = _refreshTokenGenerator.Hash(request.RefreshToken);
        var stored = await _refreshTokenRepository.GetByHashAsync(inboundHash, cancellationToken);

        if (stored is null || !stored.IsActive(DateTime.UtcNow))
            throw new UnauthorizedAccessException("Invalid refresh token");

        var user = await _userRepository.GetByIdAsync(stored.UserId, cancellationToken);
        if (user is null || !new ActiveUserSpecification().IsSatisfiedBy(user))
            throw new UnauthorizedAccessException("Invalid refresh token");

        // One-shot: revoke before issuing the new token. A replay of the
        // same raw token afterwards hits the IsActive() guard above.
        stored.Revoke();
        await _refreshTokenRepository.UpdateAsync(stored, cancellationToken);

        var lifetime = TimeSpan.FromHours(
            _configuration.GetValue<double?>("Jwt:RefreshTokenLifetimeHours")
            ?? DefaultRefreshLifetimeHours);
        var (newRaw, newHash) = _refreshTokenGenerator.Generate();
        var fresh = DomainRefreshToken.Issue(user.Id, newHash, lifetime);
        await _refreshTokenRepository.CreateAsync(fresh, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.CurrentAccessTokenJti))
        {
            await _jtiDenylist.RevokeAsync(
                request.CurrentAccessTokenJti!,
                request.CurrentAccessTokenRemainingLifetime,
                cancellationToken);
        }

        return new RefreshTokenResult
        {
            Token = _jwtTokenGenerator.GenerateToken(user),
            RefreshToken = newRaw,
            RefreshTokenExpiresAt = fresh.ExpiresAt
        };
    }
}
