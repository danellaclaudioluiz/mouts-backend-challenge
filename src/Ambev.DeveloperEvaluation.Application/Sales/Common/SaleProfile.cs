using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;

namespace Ambev.DeveloperEvaluation.Application.Sales.Common;

/// <summary>
/// Mappings shared by every Sales use case that emits a SaleDto / SaleSummaryDto.
/// </summary>
public class SaleProfile : Profile
{
    public SaleProfile()
    {
        CreateMap<SaleItem, SaleItemDto>();
        CreateMap<Sale, SaleDto>();
        CreateMap<SaleSummary, SaleSummaryDto>();
    }
}
