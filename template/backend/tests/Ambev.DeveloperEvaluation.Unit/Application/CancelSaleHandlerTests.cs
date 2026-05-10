using Ambev.DeveloperEvaluation.Application.Sales.CancelSale;
using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Ambev.DeveloperEvaluation.Unit.Domain.Entities.TestData;
using AutoMapper;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class CancelSaleHandlerTests
{
    private readonly ISaleRepository _saleRepository = Substitute.For<ISaleRepository>();
    private readonly IDomainEventPublisher _eventPublisher = Substitute.For<IDomainEventPublisher>();
    private readonly ISaleReadCache _cache = Substitute.For<ISaleReadCache>();
    private readonly IMapper _mapper = Substitute.For<IMapper>();
    private readonly CancelSaleHandler _handler;

    public CancelSaleHandlerTests()
    {
        _handler = new CancelSaleHandler(_saleRepository, _eventPublisher, _cache, _mapper);
        _mapper.Map<SaleDto>(Arg.Any<Sale>())
            .Returns(callInfo => new SaleDto { Id = callInfo.Arg<Sale>().Id });
    }

    [Fact(DisplayName = "Cancelling an unknown sale throws ResourceNotFoundException")]
    public async Task Handle_UnknownSale_Throws()
    {
        _saleRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        var act = () => _handler.Handle(new CancelSaleCommand(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<ResourceNotFoundException>();
        await _saleRepository.DidNotReceive().UpdateAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Cancelling an active sale persists, raises event, returns DTO")]
    public async Task Handle_ActiveSale_CancelsAndPublishes()
    {
        var sale = SaleTestData.GenerateValidSale(itemCount: 2);
        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        await _handler.Handle(new CancelSaleCommand(sale.Id), CancellationToken.None);

        sale.IsCancelled.Should().BeTrue();
        await _saleRepository.Received(1).UpdateAsync(sale, Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<IDomainEvent>(e => e is SaleCancelledEvent
                && ((SaleCancelledEvent)e).SaleId == sale.Id
                && ((SaleCancelledEvent)e).SaleNumber == sale.SaleNumber),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Cancelling an already-cancelled sale is idempotent and emits no extra event")]
    public async Task Handle_AlreadyCancelledSale_Idempotent()
    {
        var sale = SaleTestData.GenerateValidSale(itemCount: 1);
        sale.Cancel();
        sale.ClearDomainEvents();

        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        await _handler.Handle(new CancelSaleCommand(sale.Id), CancellationToken.None);

        await _eventPublisher.DidNotReceive().PublishAsync(
            Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }
}
