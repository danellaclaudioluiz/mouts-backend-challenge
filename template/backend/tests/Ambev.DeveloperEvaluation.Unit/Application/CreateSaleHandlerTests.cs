using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Ambev.DeveloperEvaluation.Unit.Application.TestData;
using AutoMapper;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class CreateSaleHandlerTests
{
    private readonly ISaleRepository _saleRepository = Substitute.For<ISaleRepository>();
    private readonly IDomainEventPublisher _eventPublisher = Substitute.For<IDomainEventPublisher>();
    private readonly IMapper _mapper = Substitute.For<IMapper>();
    private readonly CreateSaleHandler _handler;

    public CreateSaleHandlerTests()
    {
        _handler = new CreateSaleHandler(_saleRepository, _eventPublisher, _mapper);
        _saleRepository.CreateAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Sale>());
        _mapper.Map<SaleDto>(Arg.Any<Sale>())
            .Returns(callInfo => new SaleDto { Id = callInfo.Arg<Sale>().Id });
    }

    [Fact(DisplayName = "Valid command persists the sale and publishes SaleCreatedEvent")]
    public async Task Handle_ValidCommand_PersistsAndPublishesEvent()
    {
        var command = CreateSaleHandlerTestData.GenerateValidCommand();
        _saleRepository.GetBySaleNumberAsync(command.SaleNumber, Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        await _saleRepository.Received(1).CreateAsync(
            Arg.Is<Sale>(s => s.SaleNumber == command.SaleNumber),
            Arg.Any<CancellationToken>());
        await _eventPublisher.Received().PublishAsync(
            Arg.Is<IDomainEvent>(e => e is SaleCreatedEvent),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Duplicate SaleNumber throws ConflictException")]
    public async Task Handle_DuplicateSaleNumber_Throws()
    {
        var command = CreateSaleHandlerTestData.GenerateValidCommand();
        var existingSale = Sale.Create(
            command.SaleNumber, command.SaleDate,
            Guid.NewGuid(), "x", Guid.NewGuid(), "y");

        _saleRepository.GetBySaleNumberAsync(command.SaleNumber, Arg.Any<CancellationToken>())
            .Returns(existingSale);

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage($"*'{command.SaleNumber}'*");
        await _saleRepository.DidNotReceive().CreateAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Empty items list fails validation")]
    public async Task Handle_NoItems_ThrowsValidationException()
    {
        var command = CreateSaleHandlerTestData.GenerateValidCommand();
        command.Items.Clear();

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
    }

    [Fact(DisplayName = "Quantity above 20 fails validation before reaching the aggregate")]
    public async Task Handle_QuantityAboveCap_ThrowsValidationException()
    {
        var command = CreateSaleHandlerTestData.GenerateValidCommand();
        command.Items[0].Quantity = 21;

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
    }

    [Fact(DisplayName = "DomainEvents are cleared after publication")]
    public async Task Handle_AfterSuccess_ClearsDomainEvents()
    {
        var command = CreateSaleHandlerTestData.GenerateValidCommand();
        _saleRepository.GetBySaleNumberAsync(command.SaleNumber, Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        Sale? captured = null;
        await _saleRepository.CreateAsync(
            Arg.Do<Sale>(s => captured = s),
            Arg.Any<CancellationToken>());

        await _handler.Handle(command, CancellationToken.None);

        captured!.DomainEvents.Should().BeEmpty();
    }
}
