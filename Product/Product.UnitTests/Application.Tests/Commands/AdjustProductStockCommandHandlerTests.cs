using Application.Commands.AdjustProductStock;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Commands;

[TestFixture]
public class AdjustProductStockCommandHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private AdjustProductStockCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new AdjustProductStockCommandHandler(_persistence);
    }

    private static Product CreateActiveProduct(int stock = 10)
    {
        var product = Product.Create(
            SellerId.CreateUnique(), "Name", "Desc",
            CategoryId.CreateUnique(), Money.Create(50m, "USD"), stock, [], []);
        product.ClearDomainEvents();
        product.Approve();
        product.ClearDomainEvents();
        return product;
    }

    [Test]
    public async Task Handle_ShouldReturnSuccess_WhenProductExists()
    {
        var product = CreateActiveProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new AdjustProductStockCommand(product.Id.Value, 5), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task Handle_PositiveDelta_ShouldIncreaseStock()
    {
        var product = CreateActiveProduct(10);
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        await _handler.Handle(new AdjustProductStockCommand(product.Id.Value, 5), CancellationToken.None);

        Assert.That(product.StockQuantity, Is.EqualTo(15));
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NegativeDelta_ShouldDecreaseStock()
    {
        var product = CreateActiveProduct(10);
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        await _handler.Handle(new AdjustProductStockCommand(product.Id.Value, -3), CancellationToken.None);

        Assert.That(product.StockQuantity, Is.EqualTo(7));
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductNotFound()
    {
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _handler.Handle(new AdjustProductStockCommand(Guid.NewGuid(), 5), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("was not found"));
        await _persistence.DidNotReceive().UpdateProductAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductIsDeleted()
    {
        var product = CreateActiveProduct();
        product.Delete();
        product.ClearDomainEvents();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new AdjustProductStockCommand(product.Id.Value, -1), CancellationToken.None);

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

        Assert.ThrowsAsync<Exception>(() => _handler.Handle(new AdjustProductStockCommand(product.Id.Value, 1), CancellationToken.None));
    }

    [Test]
    public async Task Handle_NegativeDeltaExceedingStock_ShouldClampToZero()
    {
        var product = CreateActiveProduct(3);
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new AdjustProductStockCommand(product.Id.Value, -100), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(product.StockQuantity, Is.EqualTo(0));
    }
}
