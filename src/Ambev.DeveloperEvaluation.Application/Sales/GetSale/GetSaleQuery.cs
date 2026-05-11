using Ambev.DeveloperEvaluation.Application.Sales.Common;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.GetSale;

public class GetSaleQuery : IRequest<SaleDto>
{
    public Guid Id { get; set; }

    public GetSaleQuery() { }
    public GetSaleQuery(Guid id) => Id = id;
}
