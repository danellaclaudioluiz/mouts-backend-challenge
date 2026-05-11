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
        SaleItemPayloadGuard.EnsureUniqueProductIds(command.Items);

        // Cheap pre-check (single AnyAsync, no rows materialised) for the
        // common case. The unique index on SaleNumber is still the source of
        // truth: a concurrent insert that slips past this check will fail at
        // SaveChanges and be mapped to 409 by the WebApi middleware.
        if (await _saleRepository.SaleNumberExistsAsync(command.SaleNumber, cancellationToken))
            throw new ConflictException(
                $"A sale with number '{command.SaleNumber}' already exists.");

        // Items-aware factory raises SaleCreatedEvent internally with the
        // final totals — no explicit MarkCreated() call, no risk of
        // accidentally observing a half-built aggregate.
        var sale = Sale.Create(
            command.SaleNumber,
            command.SaleDate,
            command.CustomerId,
            command.CustomerName,
            command.BranchId,
            command.BranchName,
            command.Items.Select(i => new Sale.NewItem(i.ProductId, i.ProductName, i.Quantity, i.UnitPrice)));

        // Stage events first; the repository's SaveChanges commits both the
        // sale row and the outbox rows atomically.
        foreach (var domainEvent in sale.DomainEvents)
            await _eventPublisher.PublishAsync(domainEvent, cancellationToken);

        var created = await _saleRepository.CreateAsync(sale, cancellationToken);
        sale.ClearDomainEvents();

        return _mapper.Map<SaleDto>(created);
    }
}
