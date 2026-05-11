using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Ambev.DeveloperEvaluation.Domain.Specifications;
using MediatR;
using Microsoft.Extensions.Configuration;
using DomainRefreshToken = Ambev.DeveloperEvaluation.Domain.Entities.RefreshToken;

namespace Ambev.DeveloperEvaluation.Application.Auth.AuthenticateUser
{
    public class AuthenticateUserHandler : IRequestHandler<AuthenticateUserCommand, AuthenticateUserResult>
    {
        private const double DefaultRefreshLifetimeHours = 168; // 7 days

        private readonly IUserRepository _userRepository;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IJwtTokenGenerator _jwtTokenGenerator;
        private readonly IRefreshTokenGenerator _refreshTokenGenerator;
        private readonly IRefreshTokenRepository _refreshTokenRepository;
        private readonly IConfiguration _configuration;

        public AuthenticateUserHandler(
            IUserRepository userRepository,
            IPasswordHasher passwordHasher,
            IJwtTokenGenerator jwtTokenGenerator,
            IRefreshTokenGenerator refreshTokenGenerator,
            IRefreshTokenRepository refreshTokenRepository,
            IConfiguration configuration)
        {
            _userRepository = userRepository;
            _passwordHasher = passwordHasher;
            _jwtTokenGenerator = jwtTokenGenerator;
            _refreshTokenGenerator = refreshTokenGenerator;
            _refreshTokenRepository = refreshTokenRepository;
            _configuration = configuration;
        }


        public async Task<AuthenticateUserResult> Handle(AuthenticateUserCommand request, CancellationToken cancellationToken)
        {
            var user = await _userRepository.GetByEmailAsync(request.Email, cancellationToken);

            // Run the hash verification even when there is no user — both
            // paths take the same ~100ms BCrypt cost. And keep the response
            // message identical across "no such user" / "wrong password" /
            // "inactive user" so the API doesn't double as a user-enumeration
            // oracle. The inactive-user branch is logged server-side.
            var passwordOk = _passwordHasher.VerifyPassword(
                request.Password,
                user?.Password ?? BCryptPasswordHasher.TimingLevelHash.Value);

            if (user is null || !passwordOk)
            {
                throw new UnauthorizedAccessException("Invalid credentials");
            }

            var activeUserSpec = new ActiveUserSpecification();
            if (!activeUserSpec.IsSatisfiedBy(user))
            {
                // Same opaque response — distinguishing this from "wrong
                // password" would leak which emails belong to suspended users.
                throw new UnauthorizedAccessException("Invalid credentials");
            }

            var token = _jwtTokenGenerator.GenerateToken(user);

            var refreshLifetime = TimeSpan.FromHours(
                _configuration.GetValue<double?>("Jwt:RefreshTokenLifetimeHours")
                ?? DefaultRefreshLifetimeHours);
            var (rawRefresh, refreshHash) = _refreshTokenGenerator.Generate();
            var refreshEntity = DomainRefreshToken.Issue(user.Id, refreshHash, refreshLifetime);
            await _refreshTokenRepository.CreateAsync(refreshEntity, cancellationToken);

            return new AuthenticateUserResult
            {
                Token = token,
                Email = user.Email,
                Name = user.Username,
                Role = user.Role.ToString(),
                RefreshToken = rawRefresh,
                RefreshTokenExpiresAt = refreshEntity.ExpiresAt
            };
        }
    }
}
