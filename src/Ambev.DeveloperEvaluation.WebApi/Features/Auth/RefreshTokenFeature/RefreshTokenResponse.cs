namespace Ambev.DeveloperEvaluation.WebApi.Features.Auth.RefreshTokenFeature;

public sealed class RefreshTokenResponse
{
    public string Token { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiresAt { get; set; }
}
