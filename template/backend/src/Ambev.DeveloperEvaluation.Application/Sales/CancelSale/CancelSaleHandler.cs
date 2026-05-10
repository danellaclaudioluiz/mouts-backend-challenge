using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.CancelSale;

/// <summary>
/// Soft-cancels a sale. Cancelling an already-cancelled sale is idempotent
/// — the aggregate's Cancel() is a no-op in that case so no duplicate
/// SaleCancelledEvent is published.
/// </summary>
public class CancelSaleHandler : IRequestHandler<CancelSaleCommand, SaleDto>
{
    private readonly ISaleRepository _saleRepository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly IMapper _mapper;

    public CancelSaleHandler(
        ISaleRepository saleRepository,
        IDomainEventPublisher eventPublisher,
        IMapper mapper)
    {
        _saleRepository = saleRepository;
        _eventPublisher = eventPublisher;
        _mapper = mapper;
    }

    public async Task<SaleDto> Handle(CancelSaleCommand command, CancellationToken cancellationToken)
    {
        var sale = await _saleRepository.GetByIdAsync(command.Id, cancellationToken)
            ?? throw new ResourceNotFoundException("Sale", command.Id);

        sale.Cancel();

        // Stage events before save so the outbox row commits atomically with
        // the aggregate update.
        foreach (var domainEvent in sale.DomainEvents)
            await _eventPublisher.PublishAsync(domainEvent, cancellationToken);

        await _saleRepository.UpdateAsync(sale, cancellationToken);
        sale.ClearDomainEvents();

        return _mapper.Map<SaleDto>(sale);
    }
}
