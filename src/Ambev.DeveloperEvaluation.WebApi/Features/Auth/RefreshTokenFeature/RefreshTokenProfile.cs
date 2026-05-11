using AutoMapper;
using Ambev.DeveloperEvaluation.Application.Auth.RefreshToken;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Auth.RefreshTokenFeature;

public sealed class RefreshTokenProfile : Profile
{
    public RefreshTokenProfile()
    {
        CreateMap<RefreshTokenRequest, RefreshTokenCommand>();
        CreateMap<RefreshTokenResult, RefreshTokenResponse>();
    }
}
