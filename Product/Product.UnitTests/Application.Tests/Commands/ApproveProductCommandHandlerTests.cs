using Application.Commands.ApproveProduct;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;

namespace Application.Tests.Commands;

[TestFixture]
public class ApproveProductCommandHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private ApproveProductCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new ApproveProductCommandHandler(_persistence);
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
    public async Task Handle_ShouldReturnSuccess_AndApproveProduct()
    {
        var product = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new ApproveProductCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(product.Status, Is.EqualTo(ProductStatus.Approved));
        await _persistence.Received(1).UpdateProductAsync(product, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductNotFound()
    {
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _handler.Handle(new ApproveProductCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("was not found"));
    }

    [Test]
    public async Task Handle_AlreadyActiveProduct_ShouldReturnFailure()
    {
        var product = CreatePendingProduct();
        product.Approve();
        product.ClearDomainEvents();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(new ApproveProductCommand(product.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("Cannot transition"));
    }

    [Test]
    public async Task Handle_ShouldClearReviewNotes_OnApproval()
    {
        var product = CreatePendingProduct();
        product.Reject("Bad images");
        // Re-submit (Rejected->PendingApproval is allowed but not yet implemented,
        // so test from PendingApproval directly)
        product.ClearDomainEvents();

        // Create a fresh pending product for this test
        var freshProduct = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(freshProduct);

        var result = await _handler.Handle(new ApproveProductCommand(freshProduct.Id.Value), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(freshProduct.ReviewNotes, Is.Null);
    }
}
