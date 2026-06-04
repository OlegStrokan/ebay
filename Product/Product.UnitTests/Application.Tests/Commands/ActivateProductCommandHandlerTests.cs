using Application.Commands.ActivateProduct;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;

namespace Application.Tests.Commands;

[TestFixture]
public class ActivateProductCommandHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private ActivateProductCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new ActivateProductCommandHandler(_persistence);
    }

    private static Product CreatePendingProduct()
    {
        var product = Product.Create(
            SellerId.CreateUnique(), "Name", "Desc",
            CategoryId.CreateUnique(), Money.Create(10m, "USD"), 5, [], []);
        product.ClearDomainEvents();
        return product;
    }

    private static Product CreateActiveProduct()
    {
        var product = CreatePendingProduct();
        product.Approve();
        product.ClearDomainEvents();
        return product;
    }

    [Test]
    public async Task Handle_ShouldReturnSuccess_AndActivateProduct()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new ActivateProductCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(product.Status, Is.EqualTo(ProductStatus.Approved));
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductNotFound()
    {
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _handler.Handle(new ActivateProductCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("was not found"));
    }

    [Test]
    public async Task Handle_AlreadyActiveProduct_ShouldReturnFailure()
    {
        var product = CreateActiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new ActivateProductCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("Cannot transition"));
    }
}
