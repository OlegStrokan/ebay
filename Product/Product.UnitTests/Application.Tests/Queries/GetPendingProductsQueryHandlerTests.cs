using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Queries.GetPendingProducts;
using NSubstitute;

namespace Application.Tests.Queries;

[TestFixture]
public class GetPendingProductsQueryHandlerTests
{
    private IProductReadRepository _readRepo = null!;
    private GetPendingProductsQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _readRepo = Substitute.For<IProductReadRepository>();
        _handler = new GetPendingProductsQueryHandler(_readRepo);
    }

    [Test]
    public async Task Handle_ShouldReturnPagedResult()
    {
        var items = new List<ProductDetailDto>
        {
            new(Guid.NewGuid(), "Product A", "Desc", Guid.NewGuid(), "Cat",
                50m, "USD", 10, "PendingReview", Guid.NewGuid(), [], [], DateTime.UtcNow, null,
                Guid.Empty, null, "New", null),
        };
        var pagedResult = new PagedResult<ProductDetailDto>(items, 1, 1, 20);
        _readRepo.GetPendingReviewAsync(1, 20, Arg.Any<CancellationToken>()).Returns(pagedResult);

        var result = await _handler.Handle(new GetPendingProductsQuery(1, 20), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items, Has.Count.EqualTo(1));
        Assert.That(result.Value.TotalCount, Is.EqualTo(1));
        Assert.That(result.Value.Page, Is.EqualTo(1));
        Assert.That(result.Value.Size, Is.EqualTo(20));
    }

    [Test]
    public async Task Handle_ShouldReturnEmptyResult_WhenNoPendingProducts()
    {
        var pagedResult = new PagedResult<ProductDetailDto>([], 0, 1, 20);
        _readRepo.GetPendingReviewAsync(1, 20, Arg.Any<CancellationToken>()).Returns(pagedResult);

        var result = await _handler.Handle(new GetPendingProductsQuery(1, 20), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items, Is.Empty);
        Assert.That(result.Value.TotalCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Handle_ShouldPassPaginationParameters()
    {
        var pagedResult = new PagedResult<ProductDetailDto>([], 0, 3, 10);
        _readRepo.GetPendingReviewAsync(3, 10, Arg.Any<CancellationToken>()).Returns(pagedResult);

        var result = await _handler.Handle(new GetPendingProductsQuery(3, 10), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        await _readRepo.Received(1).GetPendingReviewAsync(3, 10, Arg.Any<CancellationToken>());
    }
}
