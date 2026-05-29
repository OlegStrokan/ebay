using Application.Commands.DeleteProduct;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Commands;

[TestFixture]
public class DeleteProductCommandHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private DeleteProductCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new DeleteProductCommandHandler(_persistence);
    }

    private static Product CreateActiveProduct()
    {
        var product = Product.Create(
            SellerId.CreateUnique(), "Name", "Desc",
            CategoryId.CreateUnique(), Money.Create(10m, "USD"), 5, [], []);
        product.ClearDomainEvents();
        product.Approve();
        product.ClearDomainEvents();
        return product;
    }

    [Test]
    public async Task Handle_ShouldReturnSuccess_AndDeleteProduct()
    {
        var product = CreateActiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new DeleteProductCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(product.Status, Is.EqualTo(ProductStatus.Deleted));
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductNotFound()
    {
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _handler.Handle(new DeleteProductCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("was not found"));
    }

    [Test]
    public async Task Handle_AlreadyDeletedProduct_ShouldReturnFailure()
    {
        var product = CreateActiveProduct();
        product.Delete();
        product.ClearDomainEvents();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new DeleteProductCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("Cannot transition"));
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenPersistenceThrows()
    {
        var product = CreateActiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);
        _persistence
            .UpdateProductAsync(product, Arg.Any<CancellationToken>())
            .Throws(new Exception("DB error"));

        Assert.ThrowsAsync<Exception>(() =>
            _handler.Handle(new DeleteProductCommand(product.Id.Value), CancellationToken.None));
    }
}
