using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Domain.Entities;

public class SaleTests
{
    private static Sale BuildSale() => Sale.Create(
        saleNumber: "S-0001",
        saleDate: DateTime.UtcNow,
        customerId: Guid.NewGuid(),
        customerName: "Acme Corp",
        branchId: Guid.NewGuid(),
        branchName: "Branch 1");

    [Fact(DisplayName = "AddItem with the same product merges quantities")]
    public void AddItem_SameProductTwice_MergesIntoSingleLine()
    {
        var sale = BuildSale();
        var productId = Guid.NewGuid();

        sale.AddItem(productId, "Beer", 5, 10m);
        sale.AddItem(productId, "Beer", 3, 10m);

        sale.Items.Should().HaveCount(1);
        sale.Items.Single().Quantity.Should().Be(8);
    }

    [Fact(DisplayName = "AddItem rejects merging the same product with a different unit price")]
    public void AddItem_SameProductDifferentPrice_Throws()
    {
        var sale = BuildSale();
        var productId = Guid.NewGuid();
        sale.AddItem(productId, "Beer", 5, 10m);

        var act = () => sale.AddItem(productId, "Beer", 3, 12m);

        act.Should().Throw<DomainException>().WithMessage("*unit price*");
    }

    [Fact(DisplayName = "AddItem rejects merging the same product with a different name")]
    public void AddItem_SameProductDifferentName_Throws()
    {
        var sale = BuildSale();
        var productId = Guid.NewGuid();
        sale.AddItem(productId, "Beer", 5, 10m);

        var act = () => sale.AddItem(productId, "Beer Lite", 3, 10m);

        act.Should().Throw<DomainException>().WithMessage("*product name*");
    }

    [Fact(DisplayName = "AddItem cannot bypass 20-cap by splitting lines")]
    public void AddItem_SplitLines_StillEnforces20Cap()
    {
        var sale = BuildSale();
        var productId = Guid.NewGuid();
        sale.AddItem(productId, "Beer", 15, 10m);

        var act = () => sale.AddItem(productId, "Beer", 6, 10m);

        act.Should().Throw<DomainException>()
            .WithMessage("*more than 20 identical items*");
    }

    [Fact(DisplayName = "AddItem against a cancelled sale throws")]
    public void AddItem_OnCancelledSale_Throws()
    {
        var sale = BuildSale();
        sale.AddItem(Guid.NewGuid(), "Beer", 1, 10m);
        sale.Cancel();

        var act = () => sale.AddItem(Guid.NewGuid(), "Beer", 1, 10m);

        act.Should().Throw<DomainException>().WithMessage("*cancelled*");
    }

    [Fact(DisplayName = "Cancel is idempotent and only emits one event")]
    public void Cancel_TwiceInARow_RaisesSingleEvent()
    {
        var sale = BuildSale();
        sale.AddItem(Guid.NewGuid(), "Beer", 1, 10m);

        sale.Cancel();
        sale.Cancel();

        sale.IsCancelled.Should().BeTrue();
        sale.DomainEvents.OfType<SaleCancelledEvent>().Should().ContainSingle();
    }

    [Fact(DisplayName = "CancelItem updates total to exclude that line")]
    public void CancelItem_RecalculatesTotal()
    {
        var sale = BuildSale();
        sale.AddItem(Guid.NewGuid(), "A", 2, 10m);
        var second = sale.AddItem(Guid.NewGuid(), "B", 2, 30m);

        sale.TotalAmount.Should().Be(80m);

        sale.CancelItem(second.Id);

        sale.TotalAmount.Should().Be(20m);
        sale.DomainEvents.OfType<ItemCancelledEvent>().Should().ContainSingle();
    }

    [Fact(DisplayName = "CancelItem with unknown id throws")]
    public void CancelItem_UnknownId_Throws()
    {
        var sale = BuildSale();
        sale.AddItem(Guid.NewGuid(), "A", 1, 10m);

        var act = () => sale.CancelItem(Guid.NewGuid());

        act.Should().Throw<DomainException>();
    }

    [Fact(DisplayName = "MarkCreated queues a SaleCreatedEvent with final totals")]
    public void MarkCreated_QueuesEventWithFinalTotals()
    {
        var sale = BuildSale();
        sale.AddItem(Guid.NewGuid(), "A", 4, 10m);

        sale.MarkCreated();

        var created = sale.DomainEvents.OfType<SaleCreatedEvent>().Should().ContainSingle().Subject;
        created.TotalAmount.Should().Be(sale.TotalAmount);
        created.ItemCount.Should().Be(1);
    }
}
