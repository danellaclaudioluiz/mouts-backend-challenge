using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;
using AutoMapper;
using DomainQuantity = Ambev.DeveloperEvaluation.Domain.ValueObjects.Quantity;

namespace Ambev.DeveloperEvaluation.Application.Sales.Common;

/// <summary>
/// Mappings shared by every Sales use case that emits a SaleDto / SaleSummaryDto.
/// Includes Value Object ↔ primitive converters so the entity-side
/// <see cref="Money"/> / <see cref="DomainQuantity"/> projects cleanly into
/// the wire-shaped DTOs (which stay primitive for JSON stability).
/// </summary>
public class SaleProfile : Profile
{
    public SaleProfile()
    {
        // Global converters — AutoMapper picks these up when it maps a
        // member whose source type is a VO and destination is primitive.
        CreateMap<Money, decimal>().ConvertUsing(m => m.Amount);
        CreateMap<DomainQuantity, int>().ConvertUsing(q => q.Value);

        CreateMap<SaleItem, SaleItemDto>();
        CreateMap<Sale, SaleDto>();
        CreateMap<SaleSummary, SaleSummaryDto>();
    }
}
