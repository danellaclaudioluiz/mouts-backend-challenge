using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using FluentAssertions;
using FluentValidation.TestHelper;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class CreateSaleValidatorTests
{
    private readonly CreateSaleValidator _validator = new();

    private static CreateSaleCommand ValidCommand() => new()
    {
        SaleNumber = "S-0001",
        SaleDate = DateTime.UtcNow,
        CustomerId = Guid.NewGuid(),
        CustomerName = "Acme",
        BranchId = Guid.NewGuid(),
        BranchName = "Branch",
        Items = new List<CreateSaleItemDto>
        {
            new() { ProductId = Guid.NewGuid(), ProductName = "P", Quantity = 2, UnitPrice = 10m }
        }
    };

    [Fact(DisplayName = "Valid command passes validation")]
    public void Valid_Passes()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.IsValid.Should().BeTrue();
    }

    [Fact(DisplayName = "SaleNumber is required")]
    public void EmptySaleNumber_Fails()
    {
        var cmd = ValidCommand();
        cmd.SaleNumber = "";

        _validator.TestValidate(cmd)
            .ShouldHaveValidationErrorFor(c => c.SaleNumber);
    }

    [Fact(DisplayName = "Items list cannot be empty")]
    public void EmptyItems_Fails()
    {
        var cmd = ValidCommand();
        cmd.Items.Clear();

        _validator.TestValidate(cmd)
            .ShouldHaveValidationErrorFor(c => c.Items);
    }

    [Theory(DisplayName = "Quantity must be between 1 and 20")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void OutOfRangeQuantity_Fails(int quantity)
    {
        var cmd = ValidCommand();
        cmd.Items[0].Quantity = quantity;

        _validator.TestValidate(cmd)
            .ShouldHaveValidationErrorFor("Items[0].Quantity");
    }

    [Fact(DisplayName = "UnitPrice must be greater than zero")]
    public void NonPositiveUnitPrice_Fails()
    {
        var cmd = ValidCommand();
        cmd.Items[0].UnitPrice = 0m;

        _validator.TestValidate(cmd)
            .ShouldHaveValidationErrorFor("Items[0].UnitPrice");
    }

    [Fact(DisplayName = "SaleDate before the year 2000 is rejected")]
    public void AncientSaleDate_Fails()
    {
        var cmd = ValidCommand();
        cmd.SaleDate = new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        _validator.TestValidate(cmd)
            .ShouldHaveValidationErrorFor(c => c.SaleDate);
    }

    [Fact(DisplayName = "SaleDate far in the future is rejected")]
    public void FutureSaleDate_Fails()
    {
        var cmd = ValidCommand();
        // Anchored to a fixed far-future date so the test still asserts a
        // real violation after the rule evolves (e.g. "up to 2 years"); a
        // moving DateTime.UtcNow.AddYears(1) would silently become a no-op
        // once the rule's window grew past it.
        cmd.SaleDate = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _validator.TestValidate(cmd)
            .ShouldHaveValidationErrorFor(c => c.SaleDate);
    }
}
