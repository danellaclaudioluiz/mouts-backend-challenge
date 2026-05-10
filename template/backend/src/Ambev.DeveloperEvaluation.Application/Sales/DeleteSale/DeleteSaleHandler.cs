using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.DeleteSale;

/// <summary>
/// Hard-deletes a sale and (via the configured cascade) all its items.
/// Distinct from CancelSale, which only soft-cancels for audit purposes.
/// </summary>
public class DeleteSaleHandler : IRequestHandler<DeleteSaleCommand, Unit>
{
    private readonly ISaleRepository _saleRepository;
    private readonly ISaleReadCache _cache;

    public DeleteSaleHandler(ISaleRepository saleRepository, ISaleReadCache cache)
    {
        _saleRepository = saleRepository;
        _cache = cache;
    }

    public async Task<Unit> Handle(DeleteSaleCommand command, CancellationToken cancellationToken)
    {
        if (command.ExpectedRowVersion.HasValue)
        {
            var sale = await _saleRepository.GetByIdAsync(command.Id, cancellationToken)
                ?? throw new ResourceNotFoundException("Sale", command.Id);

            if (sale.RowVersion != command.ExpectedRowVersion.Value)
                throw new PreconditionFailedException(
                    $"Sale '{command.Id}' has been modified since it was read. " +
                    "Refresh and retry with the new ETag.");
        }

        var deleted = await _saleRepository.DeleteAsync(command.Id, cancellationToken);
        if (!deleted)
            throw new ResourceNotFoundException("Sale", command.Id);

        await _cache.EvictAsync(command.Id, cancellationToken);
        return Unit.Value;
    }
}
