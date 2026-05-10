using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.GetSale;

/// <summary>
/// Read-through cache + repository fallback for fetching a Sale by id.
/// Hot sales (popular ids being polled by clients) skip the database; misses
/// hit the repository and repopulate the cache for the next call.
/// </summary>
public class GetSaleHandler : IRequestHandler<GetSaleQuery, SaleDto>
{
    private readonly ISaleRepository _saleRepository;
    private readonly ISaleReadCache _cache;
    private readonly IMapper _mapper;

    public GetSaleHandler(ISaleRepository saleRepository, ISaleReadCache cache, IMapper mapper)
    {
        _saleRepository = saleRepository;
        _cache = cache;
        _mapper = mapper;
    }

    public async Task<SaleDto> Handle(GetSaleQuery query, CancellationToken cancellationToken)
    {
        var cached = await _cache.TryGetAsync(query.Id, cancellationToken);
        if (cached is not null) return cached;

        var sale = await _saleRepository.GetByIdAsync(query.Id, cancellationToken)
            ?? throw new ResourceNotFoundException("Sale", query.Id);

        var dto = _mapper.Map<SaleDto>(sale);
        await _cache.SetAsync(dto, cancellationToken);
        return dto;
    }
}
