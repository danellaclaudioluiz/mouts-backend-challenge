using Ambev.DeveloperEvaluation.Domain.Entities;
using Bogus;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Entities.TestData;

/// <summary>
/// Bogus-based generators for valid <see cref="Sale"/> aggregates. Items are
/// intentionally added through <see cref="Sale.AddItem"/> so the generated
/// aggregate has accurate totals and discount values.
/// </summary>
public static class SaleTestData
{
    private static readonly Faker Faker = new();

    public static Sale GenerateValidSale(int itemCount = 2)
    {
        var sale = Sale.Create(
            saleNumber: Faker.Commerce.Ean13(),
            saleDate: Faker.Date.RecentOffset(7).UtcDateTime,
            customerId: Guid.NewGuid(),
            customerName: Faker.Name.FullName(),
            branchId: Guid.NewGuid(),
            branchName: Faker.Company.CompanyName());

        for (var i = 0; i < itemCount; i++)
        {
            sale.AddItem(
                productId: Guid.NewGuid(),
                productName: Faker.Commerce.ProductName(),
                quantity: Faker.Random.Int(1, 3),
                unitPrice: Faker.Random.Decimal(1m, 100m));
        }

        return sale;
    }
}
