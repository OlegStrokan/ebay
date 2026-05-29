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

    private static Product CreateActiveProduct()
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
        var product = CreateActiveProduct();
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
        var product = CreateActiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);
        var command = ValidCommand(product.Id.Value) with { Name = "" };

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        await _persistence.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenPersistenceThrows()
    {
        var product = CreateActiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);
        _persistence
            .UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("DB error"));

        Assert.ThrowsAsync<Exception>(() => _handler.Handle(ValidCommand(product.Id.Value), CancellationToken.None));
    }

    [Test]
    public async Task Handle_ShouldApplyUpdate_BeforePersisting()
    {
        var product = CreateActiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);
        var command = ValidCommand(product.Id.Value);

        await _handler.Handle(command, CancellationToken.None);

        Assert.That(product.Name, Is.EqualTo("New Name"));
        Assert.That(product.Description, Is.EqualTo("New Desc"));
        Assert.That(product.Price.Amount, Is.EqualTo(199.99m));
    }
}
