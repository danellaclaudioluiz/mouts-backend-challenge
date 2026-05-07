using Ambev.DeveloperEvaluation.Domain.Services;
using FluentValidation;

namespace Ambev.DeveloperEvaluation.Application.Sales.CreateSale;

public class CreateSaleValidator : AbstractValidator<CreateSaleCommand>
{
    public CreateSaleValidator()
    {
        RuleFor(c => c.SaleNumber).NotEmpty().MaximumLength(50);
        RuleFor(c => c.SaleDate).NotEmpty();
        RuleFor(c => c.CustomerId).NotEmpty();
        RuleFor(c => c.CustomerName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.BranchId).NotEmpty();
        RuleFor(c => c.BranchName).NotEmpty().MaximumLength(200);

        RuleFor(c => c.Items).NotEmpty().WithMessage("Sale must have at least one item.");
        RuleForEach(c => c.Items).SetValidator(new CreateSaleItemDtoValidator());
    }
}

public class CreateSaleItemDtoValidator : AbstractValidator<CreateSaleItemDto>
{
    public CreateSaleItemDtoValidator()
    {
        RuleFor(i => i.ProductId).NotEmpty();
        RuleFor(i => i.ProductName).NotEmpty().MaximumLength(200);
        RuleFor(i => i.Quantity)
            .InclusiveBetween(1, SaleItemDiscountPolicy.MaxQuantityPerProduct)
            .WithMessage($"Quantity per item must be between 1 and {SaleItemDiscountPolicy.MaxQuantityPerProduct}.");
        RuleFor(i => i.UnitPrice).GreaterThan(0m);
    }
}
