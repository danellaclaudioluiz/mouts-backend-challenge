using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Entities;

public class RefreshTokenTests
{
    [Fact(DisplayName = "Issue persists user/hash/expiry derived from lifetime")]
    public void Issue_Succeeds()
    {
        var userId = Guid.NewGuid();
        var token = RefreshToken.Issue(userId, "hash", TimeSpan.FromDays(7));

        token.UserId.Should().Be(userId);
        token.TokenHash.Should().Be("hash");
        token.RevokedAt.Should().BeNull();
        token.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromSeconds(2));
        token.IsActive(DateTime.UtcNow).Should().BeTrue();
    }

    [Fact(DisplayName = "Empty userId is rejected")]
    public void Issue_EmptyUserId_Throws()
    {
        var act = () => RefreshToken.Issue(Guid.Empty, "hash", TimeSpan.FromHours(1));
        act.Should().Throw<DomainException>().WithMessage("*bound to a user*");
    }

    [Fact(DisplayName = "Empty token hash is rejected")]
    public void Issue_EmptyHash_Throws()
    {
        var act = () => RefreshToken.Issue(Guid.NewGuid(), "", TimeSpan.FromHours(1));
        act.Should().Throw<DomainException>().WithMessage("*hash is required*");
    }

    [Fact(DisplayName = "Non-positive lifetime is rejected")]
    public void Issue_ZeroLifetime_Throws()
    {
        var act = () => RefreshToken.Issue(Guid.NewGuid(), "hash", TimeSpan.Zero);
        act.Should().Throw<DomainException>().WithMessage("*lifetime must be positive*");
    }

    [Fact(DisplayName = "Revoke is idempotent — second call is a no-op")]
    public void Revoke_Idempotent()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", TimeSpan.FromHours(1));
        token.Revoke();
        var firstRevocation = token.RevokedAt;

        token.Revoke();

        token.RevokedAt.Should().Be(firstRevocation, "second Revoke must not overwrite the timestamp");
        token.IsActive(DateTime.UtcNow).Should().BeFalse();
    }

    [Fact(DisplayName = "Expired token is not active even if not revoked")]
    public void IsActive_AfterExpiry_IsFalse()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", TimeSpan.FromMilliseconds(1));
        var future = DateTime.UtcNow.AddHours(1);
        token.IsActive(future).Should().BeFalse();
    }
}
