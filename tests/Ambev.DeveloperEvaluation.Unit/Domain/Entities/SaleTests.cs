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

    [Fact(DisplayName = "AddItem rejects a second line for the same product")]
    public void AddItem_SameProductTwice_Throws()
    {
        var sale = BuildSale();
        var productId = Guid.NewGuid();

        sale.AddItem(productId, "Beer", 5, 10m);

        var act = () => sale.AddItem(productId, "Beer", 3, 10m);

        act.Should().Throw<DomainException>().WithMessage("*already in sale*");
    }

    [Fact(DisplayName = "AddItem rejects same product even with different unit price")]
    public void AddItem_SameProductDifferentPrice_Throws()
    {
        var sale = BuildSale();
        var productId = Guid.NewGuid();
        sale.AddItem(productId, "Beer", 5, 10m);

        var act = () => sale.AddItem(productId, "Beer", 3, 12m);

        act.Should().Throw<DomainException>();
    }

    [Fact(DisplayName = "AddItem rejects same product even with different name")]
    public void AddItem_SameProductDifferentName_Throws()
    {
        var sale = BuildSale();
        var productId = Guid.NewGuid();
        sale.AddItem(productId, "Beer", 5, 10m);

        var act = () => sale.AddItem(productId, "Beer Lite", 3, 10m);

        act.Should().Throw<DomainException>();
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

        sale.TotalAmount.Amount.Should().Be(80m);

        sale.CancelItem(second.Id);

        sale.TotalAmount.Amount.Should().Be(20m);
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

    [Fact(DisplayName = "CancelItem on an already-cancelled item is a no-op and emits a single event")]
    public void CancelItem_AlreadyCancelled_IsIdempotent()
    {
        var sale = BuildSale();
        var first = sale.AddItem(Guid.NewGuid(), "A", 2, 10m);
        sale.AddItem(Guid.NewGuid(), "B", 1, 5m);

        sale.CancelItem(first.Id);
        var totalAfterFirstCancel = sale.TotalAmount;

        sale.CancelItem(first.Id);

        sale.TotalAmount.Should().Be(totalAfterFirstCancel,
            "cancelling the same item twice must not change the total again");
        sale.DomainEvents.OfType<ItemCancelledEvent>()
            .Where(e => e.ItemId == first.Id).Should().ContainSingle(
                "only the first cancellation may raise an ItemCancelledEvent");
    }

    [Fact(DisplayName = "Cancel on a sale with no items still emits SaleCancelledEvent and stays consistent")]
    public void Cancel_EmptySale_StillEmitsEvent()
    {
        var sale = BuildSale();

        sale.Cancel();

        sale.IsCancelled.Should().BeTrue();
        sale.TotalAmount.Amount.Should().Be(0m);
        sale.DomainEvents.OfType<SaleCancelledEvent>().Should().ContainSingle();
    }

    [Fact(DisplayName = "Cancel on sale cancels every still-active item too")]
    public void Cancel_CascadesToAllActiveItems()
    {
        var sale = BuildSale();
        sale.AddItem(Guid.NewGuid(), "A", 2, 10m);
        sale.AddItem(Guid.NewGuid(), "B", 3, 30m);

        sale.Cancel();

        sale.Items.Should().OnlyContain(i => i.IsCancelled);
        sale.TotalAmount.Amount.Should().Be(0m,
            "all items are cancelled so the rollup must be zero");
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
