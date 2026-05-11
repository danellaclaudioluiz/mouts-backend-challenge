using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.UpdateSale;

/// <summary>
/// Update by diff: existing items matching a command line by ProductId are
/// updated in place (preserving the SaleItem id so external integrations
/// that reference it stay valid), command items with a new ProductId are
/// added, and existing items missing from the command are removed.
/// </summary>
public class UpdateSaleHandler : IRequestHandler<UpdateSaleCommand, SaleDto>
{
    private readonly ISaleRepository _saleRepository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly ISaleReadCache _cache;
    private readonly IMapper _mapper;

    public UpdateSaleHandler(
        ISaleRepository saleRepository,
        IDomainEventPublisher eventPublisher,
        ISaleReadCache cache,
        IMapper mapper)
    {
        _saleRepository = saleRepository;
        _eventPublisher = eventPublisher;
        _cache = cache;
        _mapper = mapper;
    }

    public async Task<SaleDto> Handle(UpdateSaleCommand command, CancellationToken cancellationToken)
    {
        SaleItemPayloadGuard.EnsureUniqueProductIds(command.Items);

        var sale = await _saleRepository.GetByIdAsync(command.Id, cancellationToken)
            ?? throw new ResourceNotFoundException("Sale", command.Id);

        if (command.ExpectedRowVersion.HasValue && sale.RowVersion != command.ExpectedRowVersion.Value)
            throw new PreconditionFailedException(
                $"Sale '{command.Id}' has been modified since it was read. " +
                "Refresh and retry with the new ETag.");

        sale.UpdateHeader(
            command.SaleDate,
            command.CustomerId,
            command.CustomerName,
            command.BranchId,
            command.BranchName);

        ApplyItemDiff(sale, command.Items);
        sale.MarkModified();

        // Stage events before save so the outbox row commits atomically with
        // the aggregate update.
        foreach (var domainEvent in sale.DomainEvents)
            await _eventPublisher.PublishAsync(domainEvent, cancellationToken);

        await _saleRepository.UpdateAsync(sale, cancellationToken);
        sale.ClearDomainEvents();
        await _cache.EvictAsync(sale.Id, cancellationToken);

        return _mapper.Map<SaleDto>(sale);
    }

    private static void ApplyItemDiff(Sale sale, IReadOnlyList<CreateSale.CreateSaleItemDto> incoming)
    {
        var existingByProduct = sale.Items
            .Where(i => !i.IsCancelled)
            .ToDictionary(i => i.ProductId);

        var incomingByProduct = incoming.ToDictionary(i => i.ProductId);

        foreach (var existing in existingByProduct.Values)
        {
            if (incomingByProduct.ContainsKey(existing.ProductId))
                continue;
            sale.RemoveItem(existing.Id);
        }

        foreach (var item in incoming)
        {
            if (existingByProduct.TryGetValue(item.ProductId, out var existing))
            {
                sale.UpdateItem(existing.Id, item.ProductId, item.ProductName, item.Quantity, item.UnitPrice);
            }
            else
            {
                sale.AddItem(item.ProductId, item.ProductName, item.Quantity, item.UnitPrice);
            }
        }
    }

}
