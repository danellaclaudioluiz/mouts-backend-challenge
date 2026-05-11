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
        _saleRepository.SaleNumberExistsAsync(command.SaleNumber, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        await _saleRepository.Received(1).CreateAsync(
            Arg.Is<Sale>(s => s.SaleNumber == command.SaleNumber),
            Arg.Any<CancellationToken>());
        // Match the event by SHAPE (SaleNumber, CustomerId, BranchId, ItemCount > 0)
        // rather than just by type. Asserting only "is SaleCreatedEvent" would
        // mask a regression that ships a Guid.Empty SaleId or a 0 ItemCount
        // downstream — exactly the kind of bug a consumer cannot recover from.
        // Expression trees in NSubstitute's Arg.Is don't allow `is T t`
        // patterns with capture — fall back to a typed cast inside the
        // predicate. Still validates by shape, not just by type.
        await _eventPublisher.Received().PublishAsync(
            Arg.Is<IDomainEvent>(e => e is SaleCreatedEvent
                && ((SaleCreatedEvent)e).SaleNumber == command.SaleNumber
                && ((SaleCreatedEvent)e).CustomerId == command.CustomerId
                && ((SaleCreatedEvent)e).BranchId == command.BranchId
                && ((SaleCreatedEvent)e).ItemCount == command.Items.Count
                && ((SaleCreatedEvent)e).TotalAmount > 0m
                && ((SaleCreatedEvent)e).SaleId != Guid.Empty),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Duplicate SaleNumber throws ConflictException")]
    public async Task Handle_DuplicateSaleNumber_Throws()
    {
        var command = CreateSaleHandlerTestData.GenerateValidCommand();
        _saleRepository.SaleNumberExistsAsync(command.SaleNumber, Arg.Any<CancellationToken>())
            .Returns(true);

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage($"*'{command.SaleNumber}'*");
        await _saleRepository.DidNotReceive().CreateAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Quantity above 20 still throws DomainException at the aggregate")]
    public async Task Handle_QuantityAboveCap_ThrowsDomainException()
    {
        // Validation now lives in the MediatR pipeline (ValidationBehavior)
        // and is bypassed by direct handler tests. The aggregate's discount
        // policy is the second line of defense — qty=21 must still throw.
        var command = CreateSaleHandlerTestData.GenerateValidCommand();
        command.Items[0].Quantity = 21;
        _saleRepository.SaleNumberExistsAsync(command.SaleNumber, Arg.Any<CancellationToken>())
            .Returns(false);

        var act = () => _handler.Handle(command, CancellationToken.None);

        // Domain.ValueObjects.Quantity.From is the first guard that
        // rejects qty > 20 — its message is the canonical aggregate-side
        // wording. The MaxItemsPerSale policy constant still backs the
        // pipeline-level validator that runs in production.
        await act.Should().ThrowAsync<DomainException>()
            .WithMessage("*cannot exceed*");
    }

    [Fact(DisplayName = "DomainEvents are cleared after publication")]
    public async Task Handle_AfterSuccess_ClearsDomainEvents()
    {
        var command = CreateSaleHandlerTestData.GenerateValidCommand();
        _saleRepository.SaleNumberExistsAsync(command.SaleNumber, Arg.Any<CancellationToken>())
            .Returns(false);

        Sale? captured = null;
        await _saleRepository.CreateAsync(
            Arg.Do<Sale>(s => captured = s),
            Arg.Any<CancellationToken>());

        await _handler.Handle(command, CancellationToken.None);

        captured!.DomainEvents.Should().BeEmpty();
    }
}
