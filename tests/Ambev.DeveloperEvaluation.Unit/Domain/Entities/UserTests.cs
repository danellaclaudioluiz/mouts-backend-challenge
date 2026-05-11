using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Enums;
using Ambev.DeveloperEvaluation.Unit.Domain.Entities.TestData;
using Bogus;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Entities;

/// <summary>
/// Behavioural tests for the User aggregate. Construction goes through
/// the <see cref="User.Create"/> factory so every test starts from a
/// known-valid state; state changes route through the intent-named
/// methods (<c>Activate</c>, <c>Suspend</c>, <c>ChangeRole</c>) — the
/// public surface no longer allows arbitrary property writes.
/// </summary>
public class UserTests
{
    [Fact(DisplayName = "Suspend() then Activate() flips Status back to Active")]
    public void Given_SuspendedUser_When_Activated_Then_StatusShouldBeActive()
    {
        var user = NewValidUser();
        user.Suspend();
        user.Status.Should().Be(UserStatus.Suspended);

        user.Activate();

        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact(DisplayName = "Suspend() on an active user sets Status to Suspended")]
    public void Given_ActiveUser_When_Suspended_Then_StatusShouldBeSuspended()
    {
        var user = NewValidUser();
        user.Status.Should().Be(UserStatus.Active, "factory hard-codes Active");

        user.Suspend();

        user.Status.Should().Be(UserStatus.Suspended);
    }

    [Fact(DisplayName = "Factory output validates clean")]
    public void Given_ValidUserData_When_Validated_Then_ShouldReturnValid()
    {
        var user = NewValidUser();

        var result = user.Validate();

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact(DisplayName = "User assembled from invalid data fails the validator")]
    public void Given_InvalidUserData_When_Validated_Then_ShouldReturnInvalid()
    {
        // Bogus.Faker writes through reflection so it can hit our private
        // setters — that lets the test fabricate the "bad state" the
        // factory itself would have refused, exercising the validator's
        // negative paths.
        var user = new Faker<User>()
            .RuleFor(u => u.Username, "")
            .RuleFor(u => u.Password, UserTestData.GenerateInvalidPassword())
            .RuleFor(u => u.Email, UserTestData.GenerateInvalidEmail())
            .RuleFor(u => u.Phone, UserTestData.GenerateInvalidPhone())
            .RuleFor(u => u.Status, UserStatus.Unknown)
            .RuleFor(u => u.Role, UserRole.None)
            .Generate();

        var result = user.Validate();

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact(DisplayName = "ChangeRole(None) is rejected — only meaningful roles are accepted")]
    public void ChangeRole_None_Throws()
    {
        var user = NewValidUser();

        var act = () => user.ChangeRole(UserRole.None);

        act.Should().Throw<ArgumentException>();
    }

    [Fact(DisplayName = "ChangeRole(Admin) on a signed-up Customer flips the role and bumps UpdatedAt")]
    public void ChangeRole_PromotesCustomerToAdmin()
    {
        var user = NewValidUser();
        var before = user.UpdatedAt;

        user.ChangeRole(UserRole.Admin);

        user.Role.Should().Be(UserRole.Admin);
        user.UpdatedAt.Should().NotBe(before, "every mutation must touch UpdatedAt");
    }

    [Fact(DisplayName = "ChangePassword rewrites the hash and bumps UpdatedAt")]
    public void ChangePassword_ReplacesHash()
    {
        var user = NewValidUser();
        var originalHash = user.Password;

        user.ChangePassword("new-hash-from-bcrypt");

        user.Password.Should().Be("new-hash-from-bcrypt").And.NotBe(originalHash);
        user.UpdatedAt.Should().NotBeNull();
    }

    [Fact(DisplayName = "Factory hard-codes role=Customer and status=Active (mass-assignment defence)")]
    public void Factory_HardcodesCustomerAndActive()
    {
        var user = User.Create("alice", "bcrypt-hash", "alice@example.com", "+5511999998888");

        user.Role.Should().Be(UserRole.Customer);
        user.Status.Should().Be(UserStatus.Active);
        user.Id.Should().NotBeEmpty();
    }

    private static User NewValidUser() =>
        User.Create(
            UserTestData.GenerateValidUsername(),
            UserTestData.GenerateValidPassword(),
            UserTestData.GenerateValidEmail(),
            UserTestData.GenerateValidPhone());
}
