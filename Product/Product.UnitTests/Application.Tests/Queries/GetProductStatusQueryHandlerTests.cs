using Application.Common;
using Application.Interfaces;
using Application.Queries.GetProductStatus;
using Domain.Entities;
using Domain.ValueObjects;
using NSubstitute;

namespace Application.Tests.Queries;

[TestFixture]
public class GetProductStatusQueryHandlerTests
{
    private IProductPersistenceService _persistence = null!;
    private GetProductStatusQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _persistence = Substitute.For<IProductPersistenceService>();
        _handler = new GetProductStatusQueryHandler(_persistence);
    }

    private static (Product product, Guid sellerId) CreatePendingProduct()
    {
        var sellerId = SellerId.CreateUnique();
        var product = Product.Create(
            sellerId, "Name", "Desc",
            CategoryId.CreateUnique(), Money.Create(10m, "USD"), 5, [], []);
        product.ClearDomainEvents();
        return (product, sellerId.Value);
    }

    [Test]
    public async Task Handle_ShouldReturnStatus_WhenProductFoundAndSellerMatches()
    {
        var (product, sellerId) = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(
            new GetProductStatusQuery(product.Id.Value, sellerId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Status, Is.EqualTo("PendingReview"));
        Assert.That(result.Value.ReviewNotes, Is.Null);
    }

    [Test]
    public async Task Handle_ShouldReturnReviewNotes_WhenRejected()
    {
        var (product, sellerId) = CreatePendingProduct();
        product.Reject("Bad images");
        product.ClearDomainEvents();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(
            new GetProductStatusQuery(product.Id.Value, sellerId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Status, Is.EqualTo("Rejected"));
        Assert.That(result.Value.ReviewNotes, Is.EqualTo("Bad images"));
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenProductNotFound()
    {
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _handler.Handle(
            new GetProductStatusQuery(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("was not found"));
    }

    [Test]
    public async Task Handle_ShouldReturnFailure_WhenSellerDoesNotMatch()
    {
        var (product, _) = CreatePendingProduct();
        _persistence.GetByIdAsync(Arg.Any<ProductId>(), Arg.Any<CancellationToken>()).Returns(product);

        var result = await _handler.Handle(
            new GetProductStatusQuery(product.Id.Value, Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0], Does.Contain("permission"));
    }
}
