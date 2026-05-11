using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using Ambev.DeveloperEvaluation.Application.Sales.ListSales;
using Ambev.DeveloperEvaluation.Application.Sales.UpdateSale;
using Ambev.DeveloperEvaluation.WebApi.Features.Sales.CreateSale;
using Ambev.DeveloperEvaluation.WebApi.Features.Sales.ListSales;
using Ambev.DeveloperEvaluation.WebApi.Features.Sales.UpdateSale;
using AutoMapper;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Sales;

/// <summary>
/// Maps WebApi request shapes to Application commands/queries. The opposite
/// direction (Sale → SaleDto) lives in the Application layer's SaleProfile;
/// SaleDto is sent straight back to clients so we don't duplicate it.
/// </summary>
public class SalesRequestProfile : Profile
{
    public SalesRequestProfile()
    {
        CreateMap<CreateSaleItemRequest, CreateSaleItemDto>();
        CreateMap<CreateSaleRequest, CreateSaleCommand>();
        CreateMap<UpdateSaleRequest, UpdateSaleCommand>();
        CreateMap<ListSalesRequest, ListSalesQuery>();
    }
}
