using Ambev.DeveloperEvaluation.Application.Sales.Common;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.CancelSale;

public class CancelSaleCommand : IRequest<SaleDto>
{
    public Guid Id { get; set; }

    public CancelSaleCommand() { }
    public CancelSaleCommand(Guid id) => Id = id;
}
