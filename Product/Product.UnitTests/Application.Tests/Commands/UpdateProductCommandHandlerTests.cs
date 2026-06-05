using Application.Commands.UpdateProduct;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Commands;

[TestFixture]
public class UpdateProductCommandHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private UpdateProductCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new UpdateProductCommandHandler(_persistence);
    }

    private static Product CreatePendingProduct()
    {
        var product = Product.Create(
            SellerId.CreateUnique(), "Old Name", "Old Desc",
            CategoryId.CreateUnique(), Money.Create(10m, "USD"), 5, [], []);
        product.ClearDomainEvents();
        return product;
    }

    private static Product CreateApprovedProduct()
    {
        var product = Product.Create(
            SellerId.CreateUnique(), "Old Name", "Old Desc",
            CategoryId.CreateUnique(), Money.Create(10m, "USD"), 5, [], []);
        product.ClearDomainEvents();
        product.Approve();
        product.ClearDomainEvents();
        return product;
    }

    private static UpdateProductCommand ValidCommand(Guid productId) => new(
        ProductId: productId,
        Name: "New Name",
        Description: "New Desc",
        CategoryId: Guid.NewGuid(),
        Price: 199.99m,
        Currency: "USD",
        Attributes: [new ProductAttributeDto("size", "L")],
        ImageUrls: []);

    [Test]
    public async Task Handle_ShouldReturnSuccess_WhenProductExists()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);
        var command = ValidCommand(product.Id.Value);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductNotFound()
    {
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _handler.Handle(ValidCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("was not found"));
        await _persistence.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenNameIsEmpty()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);
        var command = ValidCommand(product.Id.Value) with { Name = "" };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        await _persistence.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenPersistenceThrows()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);
        _persistence
            .UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("DB error"));

        Assert.ThrowsAsync<Exception>(() => _handler.Handle(ValidCommand(product.Id.Value), CancellationToken.None));
    }

    [Test]
    public async Task Handle_ShouldApplyUpdate_BeforePersisting()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);
        var command = ValidCommand(product.Id.Value);

        await _handler.Handle(command, CancellationToken.None);

        Assert.That(product.Name, Is.EqualTo("New Name"));
        Assert.That(product.Description, Is.EqualTo("New Desc"));
        Assert.That(product.Price.Amount, Is.EqualTo(199.99m));
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenApprovedProductIdentityChanges()
    {
        var product = CreateApprovedProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(ValidCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Is.EqualTo("Identity updates require moderation and are temporarily disabled, available updates only for price and stock"));
        await _persistence.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldAllowPriceOnlyUpdate_WhenApprovedProductIdentityUnchanged()
    {
        var product = CreateApprovedProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var command = new UpdateProductCommand(
            ProductId: product.Id.Value,
            Name: product.Name,
            Description: product.Description,
            CategoryId: product.CategoryId.Value,
            Price: 250m,
            Currency: product.Price.Currency,
            Attributes: [],
            ImageUrls: product.ImageUrls.ToList());

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(product.Price.Amount, Is.EqualTo(250m));
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }
}
