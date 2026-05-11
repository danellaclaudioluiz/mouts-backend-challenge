using System.IdentityModel.Tokens.Jwt;
using Ambev.DeveloperEvaluation.Application.Auth.AuthenticateUser;
using Ambev.DeveloperEvaluation.Application.Auth.RefreshToken;
using Ambev.DeveloperEvaluation.WebApi.Common;
using Ambev.DeveloperEvaluation.WebApi.Features.Auth.AuthenticateUserFeature;
using Ambev.DeveloperEvaluation.WebApi.Features.Auth.RefreshTokenFeature;
using Asp.Versioning;
using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Auth;

/// <summary>
/// Controller for authentication operations.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
// All responses are wrapped in ApiResponseWithData<T> as JSON; without
// this Swagger reports 200s as text/plain (default when the request
// pipeline can't infer a single content type from controller metadata).
[Produces("application/json")]
public class AuthController : BaseController
{
    private readonly IMediator _mediator;
    private readonly IMapper _mapper;

    public AuthController(IMediator mediator, IMapper mapper)
    {
        _mediator = mediator;
        _mapper = mapper;
    }

    /// <summary>Authenticate a user (returns access + refresh tokens).</summary>
    /// <remarks>
    /// Anonymous — the caller has no token yet. Gated by the auth-strict
    /// rate limit (5 req/min/IP by default) so a password brute force
    /// is capped at ~300 attempts/h/IP regardless of the global budget.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting(Program.AuthStrictRateLimitPolicy)]
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponseWithData<AuthenticateUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AuthenticateUser(
        [FromBody] AuthenticateUserRequest request,
        CancellationToken cancellationToken)
    {
        var command = _mapper.Map<AuthenticateUserCommand>(request);
        var response = await _mediator.Send(command, cancellationToken);

        return Ok(_mapper.Map<AuthenticateUserResponse>(response));
    }

    /// <summary>Rotate a refresh token (one-shot) for a fresh access JWT.</summary>
    /// <remarks>
    /// Anonymous — by the time a client calls this its access JWT has
    /// usually already expired. If the caller does present a still-valid
    /// `Authorization` header, the handler denylists that access token's
    /// `jti` so it cannot be reused alongside the new one. Replaying the
    /// same refresh token after rotation returns **401**.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting(Program.AuthStrictRateLimitPolicy)]
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(ApiResponseWithData<RefreshTokenResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var command = _mapper.Map<RefreshTokenCommand>(request);

        // Best-effort jti extraction from the caller's current bearer
        // token. Don't validate signature here — that's the JwtBearer
        // pipeline's job; we only want the jti/exp so we can denylist
        // it after rotation.
        var auth = Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var raw = auth.Substring("Bearer ".Length).Trim();
                var jwt = new JwtSecurityTokenHandler().ReadJwtToken(raw);
                command.CurrentAccessTokenJti = jwt.Id;
                var remaining = jwt.ValidTo - DateTime.UtcNow;
                command.CurrentAccessTokenRemainingLifetime =
                    remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
            catch
            {
                // Malformed token — nothing to denylist, ignore.
            }
        }

        var response = await _mediator.Send(command, cancellationToken);
        return Ok(_mapper.Map<RefreshTokenResponse>(response));
    }
}
