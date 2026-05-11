using Ambev.DeveloperEvaluation.Application.Users.CreateUser;
using Bogus;

namespace Ambev.DeveloperEvaluation.Unit.Domain;

public static class CreateUserHandlerTestData
{
    // Role/Status intentionally not on the Faker — the handler hard-codes
    // them to Customer/Active. Generating them here would mask a future
    // regression that re-introduces them on the command.
    private static readonly Faker<CreateUserCommand> createUserHandlerFaker = new Faker<CreateUserCommand>()
        .RuleFor(u => u.Username, f => f.Internet.UserName())
        .RuleFor(u => u.Password, f => $"Test@{f.Random.Number(100, 999)}")
        .RuleFor(u => u.Email, f => f.Internet.Email())
        .RuleFor(u => u.Phone, f => $"+55{f.Random.Number(11, 99)}{f.Random.Number(100000000, 999999999)}");

    /// <summary>
    /// Generates a valid User entity with randomized data.
    /// The generated user will have all properties populated with valid values
    /// that meet the system's validation requirements.
    /// </summary>
    /// <returns>A valid User entity with randomly generated data.</returns>
    public static CreateUserCommand GenerateValidCommand()
    {
        return createUserHandlerFaker.Generate();
    }
}
