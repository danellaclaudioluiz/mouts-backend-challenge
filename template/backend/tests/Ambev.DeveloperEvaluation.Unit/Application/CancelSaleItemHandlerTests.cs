using Ambev.DeveloperEvaluation.Application.Sales.CancelSaleItem;
using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class CancelSaleItemHandlerTests
{
    private readonly ISaleRepository _saleRepository = Substitute.For<ISaleRepository>();
    private readonly IDomainEventPublisher _eventPublisher = Substitute.For<IDomainEventPublisher>();
    private readonly ISaleReadCache _cache = Substitute.For<ISaleReadCache>();
    private readonly IMapper _mapper = Substitute.For<IMapper>();
    private readonly CancelSaleItemHandler _handler;

    public CancelSaleItemHandlerTests()
    {
        _handler = new CancelSaleItemHandler(_saleRepository, _eventPublisher, _cache, _mapper);
        _mapper.Map<SaleDto>(Arg.Any<Sale>())
            .Returns(callInfo => new SaleDto { Id = callInfo.Arg<Sale>().Id });
    }

    [Fact(DisplayName = "Unknown sale id throws ResourceNotFoundException")]
    public async Task Handle_UnknownSale_Throws()
    {
        _saleRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        var act = () => _handler.Handle(
            new CancelSaleItemCommand(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        await act.Should().ThrowAsync<ResourceNotFoundException>();
    }

    [Fact(DisplayName = "Cancelling a known item recalculates total and emits ItemCancelledEvent")]
    public async Task Handle_ValidItem_RecalculatesTotalAndPublishes()
    {
        var sale = Sale.Create("S-1", DateTime.UtcNow, Guid.NewGuid(), "C", Guid.NewGuid(), "B");
        var first = sale.AddItem(Guid.NewGuid(), "A", 2, 10m);
        var second = sale.AddItem(Guid.NewGuid(), "B", 2, 30m);
        sale.ClearDomainEvents();

        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        await _handler.Handle(
            new CancelSaleItemCommand(sale.Id, second.Id),
            CancellationToken.None);

        second.IsCancelled.Should().BeTrue();
        sale.TotalAmount.Should().Be(first.TotalAmount);
        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<IDomainEvent>(e => e is ItemCancelledEvent
                && ((ItemCancelledEvent)e).SaleId == sale.Id
                && ((ItemCancelledEvent)e).ItemId == second.Id
                && ((ItemCancelledEvent)e).ProductId == second.ProductId
                && ((ItemCancelledEvent)e).Quantity == second.Quantity),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Unknown item id throws DomainException")]
    public async Task Handle_UnknownItem_Throws()
    {
        var sale = Sale.Create("S-1", DateTime.UtcNow, Guid.NewGuid(), "C", Guid.NewGuid(), "B");
        sale.AddItem(Guid.NewGuid(), "A", 1, 10m);
        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        var act = () => _handler.Handle(
            new CancelSaleItemCommand(sale.Id, Guid.NewGuid()),
            CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>();
    }
}
