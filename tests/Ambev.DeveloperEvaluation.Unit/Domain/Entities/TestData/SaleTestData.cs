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
            // Round to 2 dp — UnitPrice maps to numeric(18,2). Letting Bogus
            // emit a 28-digit decimal would yield prices the DB cannot store
            // round-trip-equal, so any assertion that compares the generated
            // value to the persisted one would be flaky.
            sale.AddItem(
                productId: Guid.NewGuid(),
                productName: Faker.Commerce.ProductName(),
                quantity: Faker.Random.Int(1, 3),
                unitPrice: Math.Round(Faker.Random.Decimal(1m, 100m), 2, MidpointRounding.AwayFromZero));
        }

        return sale;
    }
}
