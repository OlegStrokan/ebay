using Application.Commands.RejectProduct;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;

namespace Application.Tests.Commands;

[TestFixture]
public class RejectProductCommandHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private RejectProductCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new RejectProductCommandHandler(_persistence);
    }

    private static Product CreatePendingProduct()
    {
        var product = Product.Create(
            SellerId.CreateUnique(), "Name", "Desc",
            CategoryId.CreateUnique(), Money.Create(10m, "USD"), 5, [], []);
        product.ClearDomainEvents();
        return product;
    }

    [Test]
    public async Task Handle_ShouldReturnSuccess_AndRejectProduct()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(
            new RejectProductCommand(product.Id.Value, "Poor quality images"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(product.Status, Is.EqualTo(ProductStatus.Rejected));
        Assert.That(product.ReviewNotes, Is.EqualTo("Poor quality images"));
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductNotFound()
    {
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _handler.Handle(
            new RejectProductCommand(Guid.NewGuid(), "reason"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("was not found"));
    }

    [Test]
    public async Task Handle_WithEmptyReason_ShouldReturnFailure()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(
            new RejectProductCommand(product.Id.Value, "   "), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("Rejection reason cannot be empty"));
    }

    [Test]
    public async Task Handle_AlreadyRejectedProduct_ShouldReturnFailure()
    {
        var product = CreatePendingProduct();
        product.Reject("first rejection");
        product.ClearDomainEvents();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(
            new RejectProductCommand(product.Id.Value, "second rejection"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("Cannot transition"));
    }

    [Test]
    public async Task Handle_ShouldTrimReason()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        await _handler.Handle(
            new RejectProductCommand(product.Id.Value, "  bad images  "), CancellationToken.None);

        Assert.That(product.ReviewNotes, Is.EqualTo("bad images"));
    }
}
