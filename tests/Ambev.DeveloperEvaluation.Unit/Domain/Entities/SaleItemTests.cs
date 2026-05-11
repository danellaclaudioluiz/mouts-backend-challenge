using Ambev.DeveloperEvaluation.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Entities;

public class SaleItemTests
{
    private static Sale BuildSale() => Sale.Create(
        saleNumber: "S-0001",
        saleDate: DateTime.UtcNow,
        customerId: Guid.NewGuid(),
        customerName: "Acme",
        branchId: Guid.NewGuid(),
        branchName: "Branch");

    [Theory(DisplayName = "Item totals match the discount tier")]
    [InlineData(3, 10, 0, 30)]
    [InlineData(4, 10, 4, 36)]
    [InlineData(10, 10, 20, 80)]
    [InlineData(20, 10, 40, 160)]
    public void AddItem_StoresCorrectDiscountAndTotal(
        int quantity, decimal unitPrice, decimal expectedDiscount, decimal expectedTotal)
    {
        var sale = BuildSale();

        var item = sale.AddItem(Guid.NewGuid(), "P", quantity, unitPrice);

        item.Discount.Amount.Should().Be(expectedDiscount);
        item.TotalAmount.Amount.Should().Be(expectedTotal);
    }

    [Fact(DisplayName = "Item is owned by the sale that created it")]
    public void AddItem_LinksItemToSale()
    {
        var sale = BuildSale();
        var item = sale.AddItem(Guid.NewGuid(), "P", 1, 10m);

        item.SaleId.Should().Be(sale.Id);
        item.IsCancelled.Should().BeFalse();
    }
}
