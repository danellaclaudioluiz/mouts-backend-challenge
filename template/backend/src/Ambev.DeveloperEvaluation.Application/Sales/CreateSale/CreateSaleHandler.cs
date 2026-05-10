using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.CreateSale;

/// <summary>
/// Builds a Sale aggregate from the command, persists it, then publishes
/// every domain event the aggregate raised. Uniqueness on SaleNumber is
/// enforced before construction so callers get a clear conflict message
/// instead of a database constraint violation.
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

        var created = await _saleRepository.CreateAsync(sale, cancellationToken);
        await PublishAndClearEventsAsync(created, cancellationToken);

        return _mapper.Map<SaleDto>(created);
    }

    private async Task PublishAndClearEventsAsync(Sale sale, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in sale.DomainEvents)
            await _eventPublisher.PublishAsync(domainEvent, cancellationToken);
        sale.ClearDomainEvents();
    }
}
