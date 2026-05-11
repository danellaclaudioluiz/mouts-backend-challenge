namespace Ambev.DeveloperEvaluation.WebApi.Features.Auth.RefreshTokenFeature;

public sealed class RefreshTokenRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}
