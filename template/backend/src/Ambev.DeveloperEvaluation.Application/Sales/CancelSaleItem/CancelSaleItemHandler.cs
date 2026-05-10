using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.CancelSaleItem;

/// <summary>
/// Cancels a single line in a sale. Recalculates the sale total to exclude
/// the cancelled item and publishes ItemCancelledEvent. Cancelling an
/// already cancelled item is idempotent (no event).
/// </summary>
public class CancelSaleItemHandler : IRequestHandler<CancelSaleItemCommand, SaleDto>
{
    private readonly ISaleRepository _saleRepository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly IMapper _mapper;

    public CancelSaleItemHandler(
        ISaleRepository saleRepository,
        IDomainEventPublisher eventPublisher,
        IMapper mapper)
    {
        _saleRepository = saleRepository;
        _eventPublisher = eventPublisher;
        _mapper = mapper;
    }

    public async Task<SaleDto> Handle(CancelSaleItemCommand command, CancellationToken cancellationToken)
    {
        var sale = await _saleRepository.GetByIdAsync(command.SaleId, cancellationToken)
            ?? throw new ResourceNotFoundException("Sale", command.SaleId);

        sale.CancelItem(command.ItemId);

        // Stage events before save so the outbox row commits atomically with
        // the aggregate update.
        foreach (var domainEvent in sale.DomainEvents)
            await _eventPublisher.PublishAsync(domainEvent, cancellationToken);

        await _saleRepository.UpdateAsync(sale, cancellationToken);
        sale.ClearDomainEvents();

        return _mapper.Map<SaleDto>(sale);
    }
}
