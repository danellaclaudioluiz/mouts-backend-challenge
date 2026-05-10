using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.CreateSale;

/// <summary>
/// Builds a Sale aggregate from the command, stages every domain event it
/// raised through the publisher, then persists — both the aggregate and the
/// outbox messages commit in a single SaveChanges so events never get lost
/// between the row write and the dispatcher seeing them. Uniqueness on
/// SaleNumber is enforced before construction so callers get a clear conflict
/// message instead of a unique-index violation.
/// </summary>
public class CreateSaleHandler : IRequestHandler<CreateSaleCommand, SaleDto>
{
    private readonly ISaleRepository _saleRepository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly IMapper _mapper;

    public CreateSaleHandler(
        ISaleRepository saleRepository,
        IDomainEventPublisher eventPublisher,
        IMapper mapper)
    {
        _saleRepository = saleRepository;
        _eventPublisher = eventPublisher;
        _mapper = mapper;
    }

    public async Task<SaleDto> Handle(CreateSaleCommand command, CancellationToken cancellationToken)
    {
        var existing = await _saleRepository.GetBySaleNumberAsync(command.SaleNumber, cancellationToken);
        if (existing is not null)
            throw new ConflictException(
                $"A sale with number '{command.SaleNumber}' already exists.");

        var sale = Sale.Create(
            command.SaleNumber,
            command.SaleDate,
            command.CustomerId,
            command.CustomerName,
            command.BranchId,
            command.BranchName);

        foreach (var item in command.Items)
            sale.AddItem(item.ProductId, item.ProductName, item.Quantity, item.UnitPrice);

        sale.MarkCreated();

        // Stage events first; the repository's SaveChanges commits both the
        // sale row and the outbox rows atomically.
        foreach (var domainEvent in sale.DomainEvents)
            await _eventPublisher.PublishAsync(domainEvent, cancellationToken);

        var created = await _saleRepository.CreateAsync(sale, cancellationToken);
        sale.ClearDomainEvents();

        return _mapper.Map<SaleDto>(created);
    }
}
