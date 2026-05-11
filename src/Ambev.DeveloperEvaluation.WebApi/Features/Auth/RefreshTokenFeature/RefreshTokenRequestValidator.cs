using FluentValidation;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Auth.RefreshTokenFeature;

public class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.");
    }
}
