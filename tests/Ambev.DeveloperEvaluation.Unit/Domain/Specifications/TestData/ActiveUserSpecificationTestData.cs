using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Enums;
using Bogus;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Specifications.TestData;

/// <summary>
/// Bogus-backed user fixture for <c>ActiveUserSpecification</c> tests.
/// Uses <c>Faker&lt;User&gt;.RuleFor</c> (reflection-based writes) so it
/// can hit the aggregate's private setters and drop the user straight
/// into the status the test wants.
/// </summary>
public static class ActiveUserSpecificationTestData
{
    public static User GenerateUser(UserStatus status)
    {
        return new Faker<User>()
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => $"Test@{f.Random.Number(100, 999)}")
            .RuleFor(u => u.Username, f => f.Name.FirstName())
            .RuleFor(u => u.Phone, f => $"+55{f.Random.Number(11, 99)}{f.Random.Number(100000000, 999999999)}")
            .RuleFor(u => u.Role, f => f.PickRandom<UserRole>())
            .RuleFor(u => u.Status, status)
            .Generate();
    }
}
