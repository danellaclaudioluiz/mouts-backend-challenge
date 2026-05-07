using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using FluentValidation;
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
        var validator = new ListSalesValidator();
        var validationResult = await validator.ValidateAsync(query, cancellationToken);
        if (!validationResult.IsValid)
            throw new ValidationException(validationResult.Errors);

        var filter = new SaleListFilter
        {
            Page = query.Page,
            Size = query.Size,
            Order = query.Order,
            SaleNumber = query.SaleNumber,
            FromDate = query.FromDate,
            ToDate = query.ToDate,
            CustomerId = query.CustomerId,
            BranchId = query.BranchId,
            IsCancelled = query.IsCancelled
        };

        var (sales, totalCount) = await _saleRepository.ListAsync(filter, cancellationToken);

        return new ListSalesResult
        {
            Items = sales.Select(s => _mapper.Map<SaleDto>(s)).ToList(),
            TotalCount = totalCount,
            Page = query.Page,
            Size = query.Size
        };
    }
}
