using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.UpdateSale;

/// <summary>
/// Full-replace update: header fields are overwritten and the item set is
/// rebuilt from scratch (existing items removed, then re-added from the
/// command). Raises a single SaleModifiedEvent at the end so listeners see
/// the post-mutation state.
/// </summary>
public class UpdateSaleHandler : IRequestHandler<UpdateSaleCommand, SaleDto>
{
    private readonly ISaleRepository _saleRepository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly IMapper _mapper;

    public UpdateSaleHandler(
        ISaleRepository saleRepository,
        IDomainEventPublisher eventPublisher,
        IMapper mapper)
    {
        _saleRepository = saleRepository;
        _eventPublisher = eventPublisher;
        _mapper = mapper;
    }

    public async Task<SaleDto> Handle(UpdateSaleCommand command, CancellationToken cancellationToken)
    {
        var sale = await _saleRepository.GetByIdAsync(command.Id, cancellationToken)
            ?? throw new ResourceNotFoundException("Sale", command.Id);

        sale.UpdateHeader(
            command.SaleDate,
            command.CustomerId,
            command.CustomerName,
            command.BranchId,
            command.BranchName);

        foreach (var existing in sale.Items.ToList())
            sale.RemoveItem(existing.Id);

        foreach (var item in command.Items)
            sale.AddItem(item.ProductId, item.ProductName, item.Quantity, item.UnitPrice);

        sale.MarkModified();

        await _saleRepository.UpdateAsync(sale, cancellationToken);
        await PublishAndClearEventsAsync(sale, cancellationToken);

        return _mapper.Map<SaleDto>(sale);
    }

    private async Task PublishAndClearEventsAsync(Sale sale, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in sale.DomainEvents)
            await _eventPublisher.PublishAsync(domainEvent, cancellationToken);
        sale.ClearDomainEvents();
    }
}
