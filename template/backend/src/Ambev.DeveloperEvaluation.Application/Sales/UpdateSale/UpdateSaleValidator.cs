using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using FluentValidation;

namespace Ambev.DeveloperEvaluation.Application.Sales.UpdateSale;

public class UpdateSaleValidator : AbstractValidator<UpdateSaleCommand>
{
    public UpdateSaleValidator() : this(TimeProvider.System)
    {
    }

    public UpdateSaleValidator(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        RuleFor(c => c.Id).NotEmpty();
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
        RuleForEach(c => c.Items).SetValidator(new CreateSaleItemDtoValidator());
    }
}
