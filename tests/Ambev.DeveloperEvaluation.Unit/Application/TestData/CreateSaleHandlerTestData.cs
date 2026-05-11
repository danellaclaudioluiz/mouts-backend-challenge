using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using Bogus;

namespace Ambev.DeveloperEvaluation.Unit.Application.TestData;

public static class CreateSaleHandlerTestData
{
    private static readonly Faker<CreateSaleItemDto> ItemFaker = new Faker<CreateSaleItemDto>()
        .RuleFor(i => i.ProductId, _ => Guid.NewGuid())
        .RuleFor(i => i.ProductName, f => f.Commerce.ProductName())
        .RuleFor(i => i.Quantity, f => f.Random.Int(1, 3))
        .RuleFor(i => i.UnitPrice, f => Math.Round(f.Random.Decimal(1m, 100m), 2));

    private static readonly Faker<CreateSaleCommand> CommandFaker = new Faker<CreateSaleCommand>()
        .RuleFor(c => c.SaleNumber, f => f.Commerce.Ean13())
        .RuleFor(c => c.SaleDate, f => f.Date.RecentOffset(7).UtcDateTime)
        .RuleFor(c => c.CustomerId, _ => Guid.NewGuid())
        .RuleFor(c => c.CustomerName, f => f.Name.FullName())
        .RuleFor(c => c.BranchId, _ => Guid.NewGuid())
        .RuleFor(c => c.BranchName, f => f.Company.CompanyName())
        .RuleFor(c => c.Items, _ => ItemFaker.Generate(2));

    public static CreateSaleCommand GenerateValidCommand() => CommandFaker.Generate();
}
