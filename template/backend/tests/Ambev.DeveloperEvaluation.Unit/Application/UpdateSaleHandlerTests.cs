using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using Ambev.DeveloperEvaluation.Application.Sales.UpdateSale;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class UpdateSaleHandlerTests
{
    private readonly ISaleRepository _saleRepository = Substitute.For<ISaleRepository>();
    private readonly IDomainEventPublisher _eventPublisher = Substitute.For<IDomainEventPublisher>();
    private readonly IMapper _mapper = Substitute.For<IMapper>();
    private readonly UpdateSaleHandler _handler;

    public UpdateSaleHandlerTests()
    {
        _handler = new UpdateSaleHandler(_saleRepository, _eventPublisher, _mapper);
        _mapper.Map<SaleDto>(Arg.Any<Sale>())
            .Returns(callInfo => new SaleDto { Id = callInfo.Arg<Sale>().Id });
    }

    private static Sale BuildExistingSale()
    {
        var sale = Sale.Create("S-1", DateTime.UtcNow, Guid.NewGuid(), "C", Guid.NewGuid(), "B");
        sale.AddItem(Guid.NewGuid(), "A", 2, 10m);
        sale.ClearDomainEvents();
        return sale;
    }

    [Fact(DisplayName = "Unknown sale id throws ResourceNotFoundException")]
    public async Task Handle_UnknownSale_Throws()
    {
        _saleRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        var act = () => _handler.Handle(
            new UpdateSaleCommand
            {
                Id = Guid.NewGuid(),
                SaleDate = DateTime.UtcNow,
                CustomerId = Guid.NewGuid(),
                CustomerName = "C",
                BranchId = Guid.NewGuid(),
                BranchName = "B",
                Items = new List<CreateSaleItemDto>
                {
                    new() { ProductId = Guid.NewGuid(), ProductName = "P", Quantity = 1, UnitPrice = 1m }
                }
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<ResourceNotFoundException>();
    }

    [Fact(DisplayName = "Existing items keep their id when only quantity changes")]
    public async Task Handle_DiffByProductId_PreservesItemIds()
    {
        var sale = BuildExistingSale();
        var existingItem = sale.Items.Single();
        var existingItemId = existingItem.Id;
        var existingProductId = existingItem.ProductId;

        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        await _handler.Handle(new UpdateSaleCommand
        {
            Id = sale.Id,
            SaleDate = DateTime.UtcNow,
            CustomerId = sale.CustomerId,
            CustomerName = sale.CustomerName,
            BranchId = sale.BranchId,
            BranchName = sale.BranchName,
            Items = new List<CreateSaleItemDto>
            {
                new() { ProductId = existingProductId, ProductName = "A", Quantity = 5, UnitPrice = 10m }
            }
        }, CancellationToken.None);

        sale.Items.Should().HaveCount(1);
        sale.Items.Single().Id.Should().Be(existingItemId);
        sale.Items.Single().Quantity.Should().Be(5);
    }

    [Fact(DisplayName = "Items missing from the payload are removed")]
    public async Task Handle_MissingItem_IsRemoved()
    {
        var sale = BuildExistingSale();
        sale.AddItem(Guid.NewGuid(), "B", 1, 5m);
        sale.ClearDomainEvents();

        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        var keep = sale.Items.First();

        await _handler.Handle(new UpdateSaleCommand
        {
            Id = sale.Id,
            SaleDate = DateTime.UtcNow,
            CustomerId = sale.CustomerId,
            CustomerName = sale.CustomerName,
            BranchId = sale.BranchId,
            BranchName = sale.BranchName,
            Items = new List<CreateSaleItemDto>
            {
                new() { ProductId = keep.ProductId, ProductName = keep.ProductName, Quantity = keep.Quantity, UnitPrice = keep.UnitPrice }
            }
        }, CancellationToken.None);

        sale.Items.Should().ContainSingle()
            .Which.ProductId.Should().Be(keep.ProductId);
    }

    [Fact(DisplayName = "Duplicate ProductIds in the payload throw DomainException")]
    public async Task Handle_DuplicateProductIds_Throws()
    {
        var sale = BuildExistingSale();
        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        var productId = Guid.NewGuid();

        var act = () => _handler.Handle(new UpdateSaleCommand
        {
            Id = sale.Id,
            SaleDate = DateTime.UtcNow,
            CustomerId = sale.CustomerId,
            CustomerName = sale.CustomerName,
            BranchId = sale.BranchId,
            BranchName = sale.BranchName,
            Items = new List<CreateSaleItemDto>
            {
                new() { ProductId = productId, ProductName = "X", Quantity = 1, UnitPrice = 1m },
                new() { ProductId = productId, ProductName = "X", Quantity = 2, UnitPrice = 1m }
            }
        }, CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*more than once*");
    }

    [Fact(DisplayName = "Successful update publishes SaleModifiedEvent and clears events")]
    public async Task Handle_Success_StagesAndClearsEvents()
    {
        var sale = BuildExistingSale();
        var productId = sale.Items.Single().ProductId;
        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        await _handler.Handle(new UpdateSaleCommand
        {
            Id = sale.Id,
            SaleDate = DateTime.UtcNow,
            CustomerId = sale.CustomerId,
            CustomerName = "Updated",
            BranchId = sale.BranchId,
            BranchName = sale.BranchName,
            Items = new List<CreateSaleItemDto>
            {
                new() { ProductId = productId, ProductName = "A", Quantity = 3, UnitPrice = 10m }
            }
        }, CancellationToken.None);

        await _eventPublisher.Received().PublishAsync(
            Arg.Is<IDomainEvent>(e => e is SaleModifiedEvent),
            Arg.Any<CancellationToken>());
        sale.DomainEvents.Should().BeEmpty();
    }
}
