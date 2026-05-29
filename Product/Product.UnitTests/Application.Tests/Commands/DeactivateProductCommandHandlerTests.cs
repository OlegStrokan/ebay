using Application.Commands.DeactivateProduct;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;

namespace Application.Tests.Commands;

[TestFixture]
public class DeactivateProductCommandHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private DeactivateProductCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new DeactivateProductCommandHandler(_persistence);
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

    private static Product CreateInactiveProduct()
    {
        var product = CreateActiveProduct();
        product.Deactivate();
        product.ClearDomainEvents();
        return product;
    }

    [Test]
    public async Task Handle_ShouldReturnSuccess_AndDeactivateProduct()
    {
        var product = CreateActiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new DeactivateProductCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(product.Status, Is.EqualTo(ProductStatus.Inactive));
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductNotFound()
    {
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _handler.Handle(new DeactivateProductCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("was not found"));
    }

    [Test]
    public async Task Handle_DraftProduct_ShouldReturnFailure()
    {
        var product = CreateInactiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new DeactivateProductCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("Cannot transition"));
    }
}
