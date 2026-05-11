using Ambev.DeveloperEvaluation.Domain.Services;
using FluentValidation;

namespace Ambev.DeveloperEvaluation.Application.Sales.CreateSale;

public class CreateSaleValidator : AbstractValidator<CreateSaleCommand>
{
    public CreateSaleValidator() : this(TimeProvider.System)
    {
    }

    public CreateSaleValidator(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        RuleFor(c => c.SaleNumber).NotEmpty().MaximumLength(50);
        RuleFor(c => c.SaleDate)
            .GreaterThan(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .WithMessage("Sale date must be a real date (after 2000-01-01).")
            .LessThanOrEqualTo(_ => now.AddDays(1))
            .WithMessage("Sale date cannot be in the future.");
        RuleFor(c => c.CustomerId).NotEmpty();
        RuleFor(c => c.CustomerName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.BranchId).NotEmpty();
        RuleFor(c => c.BranchName).NotEmpty().MaximumLength(200);

        RuleFor(c => c.Items).NotEmpty().WithMessage("Sale must have at least one item.");
        RuleFor(c => c.Items)
            .Must(items => items == null || items.Count <= SaleItemDiscountPolicy.MaxItemsPerSale)
            .WithMessage($"Sale cannot have more than {SaleItemDiscountPolicy.MaxItemsPerSale} items.");
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
