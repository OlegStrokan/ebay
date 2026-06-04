using Application.Commands.CreateProduct;
using Application.Commands.UpdateProduct;
using Application.Commands.UpdateProductStock;
using Application.DTOs;
using FluentValidation.TestHelper;

namespace Application.Tests.Validators;

[TestFixture]
public class CreateProductCommandValidatorTests
{
    private readonly CreateProductCommandValidator _validator = new();

    private static CreateProductCommand ValidCommand() => new(
        SellerId: Guid.NewGuid(),
        Name: "Valid Product Name",
        Description: "A description",
        Price: 10m,
        Currency: "USD",
        InitialStock: 0,
        Attributes: [],
        ImageUrls: []);

    [Test]
    public void Validate_ValidCommand_ShouldHaveNoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_EmptySellerId_ShouldHaveError()
    {
        var result = _validator.TestValidate(ValidCommand() with { SellerId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.SellerId)
            .WithErrorMessage("Seller ID is required.");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Validate_EmptyName_ShouldHaveError(string name)
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = name });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Test]
    public void Validate_NameExceeding200Chars_ShouldHaveError()
    {
        var longName = new string('x', 201);
        var result = _validator.TestValidate(ValidCommand() with { Name = longName });
        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Product name must not exceed 200 characters.");
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void Validate_PriceNotGreaterThanZero_ShouldHaveError(decimal price)
    {
        var result = _validator.TestValidate(ValidCommand() with { Price = price });
        result.ShouldHaveValidationErrorFor(x => x.Price)
            .WithErrorMessage("Price must be greater than zero.");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Validate_EmptyCurrency_ShouldHaveError(string currency)
    {
        var result = _validator.TestValidate(ValidCommand() with { Currency = currency });
        result.ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [TestCase("US")]
    [TestCase("USDD")]
    public void Validate_CurrencyNotThreeChars_ShouldHaveError(string currency)
    {
        var result = _validator.TestValidate(ValidCommand() with { Currency = currency });
        result.ShouldHaveValidationErrorFor(x => x.Currency)
            .WithErrorMessage("Currency must be a 3-character ISO code.");
    }

    [Test]
    public void Validate_NegativeInitialStock_ShouldHaveError()
    {
        var result = _validator.TestValidate(ValidCommand() with { InitialStock = -1 });
        result.ShouldHaveValidationErrorFor(x => x.InitialStock)
            .WithErrorMessage("Initial stock cannot be negative.");
    }

    [Test]
    public void Validate_ZeroInitialStock_ShouldBeValid()
    {
        var result = _validator.TestValidate(ValidCommand() with { InitialStock = 0 });
        result.ShouldNotHaveValidationErrorFor(x => x.InitialStock);
    }
}

[TestFixture]
public class UpdateProductCommandValidatorTests
{
    private readonly UpdateProductCommandValidator _validator = new();

    private static UpdateProductCommand ValidCommand() => new(
        ProductId: Guid.NewGuid(),
        Name: "Valid Name",
        Description: "Desc",
        CategoryId: Guid.NewGuid(),
        Price: 10m,
        Currency: "EUR",
        Attributes: [],
        ImageUrls: []);

    [Test]
    public void Validate_ValidCommand_ShouldHaveNoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_EmptyProductId_ShouldHaveError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ProductId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.ProductId)
            .WithErrorMessage("Product ID is required.");
    }

    [Test]
    public void Validate_EmptyCategoryId_ShouldHaveError()
    {
        var result = _validator.TestValidate(ValidCommand() with { CategoryId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.CategoryId)
            .WithErrorMessage("Category ID is required.");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Validate_EmptyName_ShouldHaveError(string name)
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = name });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Test]
    public void Validate_NameExceeding200Chars_ShouldHaveError()
    {
        var longName = new string('a', 201);
        var result = _validator.TestValidate(ValidCommand() with { Name = longName });
        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Product name must not exceed 200 characters.");
    }

    [TestCase(0)]
    [TestCase(-50)]
    public void Validate_PriceNotGreaterThanZero_ShouldHaveError(decimal price)
    {
        var result = _validator.TestValidate(ValidCommand() with { Price = price });
        result.ShouldHaveValidationErrorFor(x => x.Price);
    }

    [TestCase("US")]
    [TestCase("EURO")]
    public void Validate_CurrencyNotThreeChars_ShouldHaveError(string currency)
    {
        var result = _validator.TestValidate(ValidCommand() with { Currency = currency });
        result.ShouldHaveValidationErrorFor(x => x.Currency);
    }
}

[TestFixture]
public class UpdateProductStockCommandValidatorTests
{
    private readonly UpdateProductStockCommandValidator _validator = new();

    [Test]
    public void Validate_ValidCommand_ShouldHaveNoErrors()
    {
        var result = _validator.TestValidate(new UpdateProductStockCommand(Guid.NewGuid(), 0));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_EmptyProductId_ShouldHaveError()
    {
        var result = _validator.TestValidate(new UpdateProductStockCommand(Guid.Empty, 5));
        result.ShouldHaveValidationErrorFor(x => x.ProductId)
            .WithErrorMessage("Product ID is required.");
    }

    [Test]
    public void Validate_NegativeQuantity_ShouldHaveError()
    {
        var result = _validator.TestValidate(new UpdateProductStockCommand(Guid.NewGuid(), -1));
        result.ShouldHaveValidationErrorFor(x => x.NewQuantity)
            .WithErrorMessage("Stock quantity cannot be negative.");
    }

    [Test]
    public void Validate_ZeroQuantity_ShouldBeValid()
    {
        var result = _validator.TestValidate(new UpdateProductStockCommand(Guid.NewGuid(), 0));
        result.ShouldNotHaveValidationErrorFor(x => x.NewQuantity);
    }

    [Test]
    public void Validate_PositiveQuantity_ShouldBeValid()
    {
        var result = _validator.TestValidate(new UpdateProductStockCommand(Guid.NewGuid(), 100));
        result.ShouldNotHaveAnyValidationErrors();
    }
}
