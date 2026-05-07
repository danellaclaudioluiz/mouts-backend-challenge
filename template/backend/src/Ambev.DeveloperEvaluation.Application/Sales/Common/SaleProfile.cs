using Ambev.DeveloperEvaluation.Domain.Entities;
using AutoMapper;

namespace Ambev.DeveloperEvaluation.Application.Sales.Common;

/// <summary>
/// Mappings shared by every Sales use case that emits a SaleDto.
/// </summary>
public class SaleProfile : Profile
{
    public SaleProfile()
    {
        CreateMap<SaleItem, SaleItemDto>();
        CreateMap<Sale, SaleDto>();
    }
}
