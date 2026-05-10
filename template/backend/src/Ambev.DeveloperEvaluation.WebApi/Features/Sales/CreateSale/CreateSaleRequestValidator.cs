using Ambev.DeveloperEvaluation.Domain.Services;
using FluentValidation;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Sales.CreateSale;

public class CreateSaleRequestValidator : AbstractValidator<CreateSaleRequest>
{
    public CreateSaleRequestValidator()
    {
        RuleFor(r => r.SaleNumber).NotEmpty().MaximumLength(50);
        RuleFor(r => r.SaleDate).NotEmpty();
        RuleFor(r => r.CustomerId).NotEmpty();
        RuleFor(r => r.CustomerName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.BranchName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Items).NotEmpty();
        RuleForEach(r => r.Items).SetValidator(new CreateSaleItemRequestValidator());
    }
}

public class CreateSaleItemRequestValidator : AbstractValidator<CreateSaleItemRequest>
{
    public CreateSaleItemRequestValidator()
    {
        RuleFor(i => i.ProductId).NotEmpty();
        RuleFor(i => i.ProductName).NotEmpty().MaximumLength(200);
        RuleFor(i => i.Quantity)
            .InclusiveBetween(1, SaleItemDiscountPolicy.MaxQuantityPerProduct);
        RuleFor(i => i.UnitPrice).GreaterThan(0m);
    }
}
