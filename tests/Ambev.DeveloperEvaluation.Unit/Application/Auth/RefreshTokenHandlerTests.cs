using Ambev.DeveloperEvaluation.Application.Auth.RefreshToken;
using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Enums;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;
using DomainRefreshToken = Ambev.DeveloperEvaluation.Domain.Entities.RefreshToken;

namespace Ambev.DeveloperEvaluation.Unit.Application.Auth;

public class RefreshTokenHandlerTests
{
    private readonly IRefreshTokenRepository _refreshRepo = Substitute.For<IRefreshTokenRepository>();
    private readonly IRefreshTokenGenerator _refreshGen = Substitute.For<IRefreshTokenGenerator>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IJwtTokenGenerator _jwtGen = Substitute.For<IJwtTokenGenerator>();
    private readonly IJtiDenylist _denylist = Substitute.For<IJtiDenylist>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly RefreshTokenHandler _handler;

    public RefreshTokenHandlerTests()
    {
        _handler = new RefreshTokenHandler(
            _refreshRepo, _refreshGen, _userRepo, _jwtGen, _denylist, _configuration);
    }

    [Fact(DisplayName = "Unknown raw token surfaces as opaque UnauthorizedAccessException")]
    public async Task UnknownToken_ThrowsUnauthorized()
    {
        _refreshGen.Hash("bogus").Returns("hash-of-bogus");
        _refreshRepo.GetByHashAsync("hash-of-bogus", Arg.Any<CancellationToken>())
            .Returns((DomainRefreshToken?)null);

        var act = async () => await _handler.Handle(
            new RefreshTokenCommand { RefreshToken = "bogus" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Invalid refresh token");
    }

    [Fact(DisplayName = "Already-revoked token is rejected (one-shot rotation)")]
    public async Task RevokedToken_ThrowsUnauthorized()
    {
        var user = ValidUser();
        var stored = DomainRefreshToken.Issue(user.Id, "hash", TimeSpan.FromDays(7));
        stored.Revoke();
        _refreshGen.Hash("raw").Returns("hash");
        _refreshRepo.GetByHashAsync("hash", Arg.Any<CancellationToken>()).Returns(stored);

        var act = async () => await _handler.Handle(
            new RefreshTokenCommand { RefreshToken = "raw" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact(DisplayName = "Happy path: revokes old row, issues new pair, denylists prior jti")]
    public async Task HappyPath_RotatesAndDenylists()
    {
        var user = ValidUser();
        var stored = DomainRefreshToken.Issue(user.Id, "hash", TimeSpan.FromDays(7));
        _refreshGen.Hash("raw").Returns("hash");
        _refreshGen.Generate().Returns(("new-raw", "new-hash"));
        _refreshRepo.GetByHashAsync("hash", Arg.Any<CancellationToken>()).Returns(stored);
        _userRepo.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _jwtGen.GenerateToken(user).Returns("fresh-jwt");

        var result = await _handler.Handle(
            new RefreshTokenCommand
            {
                RefreshToken = "raw",
                CurrentAccessTokenJti = "old-jti",
                CurrentAccessTokenRemainingLifetime = TimeSpan.FromMinutes(10)
            },
            CancellationToken.None);

        result.Token.Should().Be("fresh-jwt");
        result.RefreshToken.Should().Be("new-raw");
        stored.RevokedAt.Should().NotBeNull("old refresh row must be one-shot revoked");
        await _refreshRepo.Received(1).UpdateAsync(stored, Arg.Any<CancellationToken>());
        await _refreshRepo.Received(1).CreateAsync(
            Arg.Is<DomainRefreshToken>(t => t.TokenHash == "new-hash" && t.UserId == user.Id),
            Arg.Any<CancellationToken>());
        await _denylist.Received(1).RevokeAsync(
            "old-jti", TimeSpan.FromMinutes(10), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Suspended user — refresh is rejected even with a valid token row")]
    public async Task SuspendedUser_ThrowsUnauthorized()
    {
        var user = ValidUser();
        user.Suspend();
        var stored = DomainRefreshToken.Issue(user.Id, "hash", TimeSpan.FromDays(7));
        _refreshGen.Hash("raw").Returns("hash");
        _refreshRepo.GetByHashAsync("hash", Arg.Any<CancellationToken>()).Returns(stored);
        _userRepo.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var act = async () => await _handler.Handle(
            new RefreshTokenCommand { RefreshToken = "raw" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private static User ValidUser() =>
        User.Create("alice", "bcrypt-hash", "alice@example.com", "+5511999998888");
}
