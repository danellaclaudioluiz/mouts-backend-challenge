using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.ListSales;

public class ListSalesHandler : IRequestHandler<ListSalesQuery, ListSalesResult>
{
    private readonly ISaleRepository _saleRepository;
    private readonly IMapper _mapper;

    public ListSalesHandler(ISaleRepository saleRepository, IMapper mapper)
    {
        _saleRepository = saleRepository;
        _mapper = mapper;
    }

    public async Task<ListSalesResult> Handle(ListSalesQuery query, CancellationToken cancellationToken)
    {
        var filter = new SaleListFilter
        {
            Page = query.Page,
            Size = query.Size,
            Order = query.Order,
            Cursor = query.Cursor,
            SaleNumber = query.SaleNumber,
            FromDate = query.FromDate,
            ToDate = query.ToDate,
            CustomerId = query.CustomerId,
            BranchId = query.BranchId,
            IsCancelled = query.IsCancelled
        };

        var page = await _saleRepository.ListAsync(filter, cancellationToken);

        return new ListSalesResult
        {
            Items = page.Items.Select(s => _mapper.Map<SaleSummaryDto>(s)).ToList(),
            TotalCount = page.TotalCount,
            NextCursor = page.NextCursor,
            Page = query.Page,
            Size = query.Size
        };
    }
}
