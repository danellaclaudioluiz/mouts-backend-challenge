using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Auth.RefreshToken;

public sealed class RefreshTokenCommand : IRequest<RefreshTokenResult>
{
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// jti claim of the access token the client is currently presenting
    /// (extracted by the WebApi layer from the Authorization header,
    /// if any). When present, the handler denylists it after rotation
    /// so the old access token cannot continue to be used until its
    /// natural expiry.
    /// </summary>
    public string? CurrentAccessTokenJti { get; set; }

    /// <summary>
    /// Remaining lifetime of the current access token (so the denylist
    /// entry can be evicted automatically once it would expire anyway).
    /// </summary>
    public TimeSpan CurrentAccessTokenRemainingLifetime { get; set; }
}
