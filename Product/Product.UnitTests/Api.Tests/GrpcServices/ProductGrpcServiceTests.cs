using Api.GrpcServices;
using Api.Mappers;
using Application.Common;
using Application.DTOs;
using Application.Queries.GetListingsForCatalogItem;
using Application.Queries.GetProduct;
using Application.Queries.GetProductPrices;
using Application.Queries.GetProducts;
using Domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Grpc.Core;
using MediatR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Protos.Product;

namespace Api.Tests.GrpcServices;

[TestFixture]
public class ProductGrpcServiceTests
{
    private IMediator _mediator = null!;
    private ILogger<ProductGrpcHandler> _productLogger = null!;
    private ILogger<ListingGrpcHandler> _listingLogger = null!;
    private IValidator<GetListingPricesRequest> _getListingPricesValidator = null!;
    private IValidator<GetListingsRequest> _getListingsValidator = null!;
    private IValidator<GetListingRequest> _getListingValidator = null!;
    private IValidator<CreateCatalogItemWithListingRequest> _createCatalogItemWithListingValidator = null!;
    private IValidator<UpdateCatalogItemAndListingRequest> _updateCatalogItemAndListingValidator = null!;
    private IValidator<DeleteListingRequest> _deleteListingValidator = null!;
    private IValidator<ActivateListingRequest> _activateListingValidator = null!;
    private IValidator<DeactivateListingRequest> _deactivateListingValidator = null!;
    private IValidator<UpdateListingStockRequest> _updateListingStockValidator = null!;
    private ServerCallContext _callContext = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _productLogger = Substitute.For<ILogger<ProductGrpcHandler>>();
        _listingLogger = Substitute.For<ILogger<ListingGrpcHandler>>();
        _getListingPricesValidator = Substitute.For<IValidator<GetListingPricesRequest>>();
        _getListingsValidator = Substitute.For<IValidator<GetListingsRequest>>();
        _getListingValidator = Substitute.For<IValidator<GetListingRequest>>();
        _createCatalogItemWithListingValidator = Substitute.For<IValidator<CreateCatalogItemWithListingRequest>>();
        _updateCatalogItemAndListingValidator = Substitute.For<IValidator<UpdateCatalogItemAndListingRequest>>();
        _deleteListingValidator = Substitute.For<IValidator<DeleteListingRequest>>();
        _activateListingValidator = Substitute.For<IValidator<ActivateListingRequest>>();
        _deactivateListingValidator = Substitute.For<IValidator<DeactivateListingRequest>>();
        _updateListingStockValidator = Substitute.For<IValidator<UpdateListingStockRequest>>();
        _callContext = Substitute.For<ServerCallContext>();
    }

    private ProductGrpcService BuildService()
    {
        var productHandler = new ProductGrpcHandler(_mediator, _productLogger);
        var listingHandler = new ListingGrpcHandler(_mediator, _listingLogger,
            _getListingPricesValidator, _getListingsValidator, _getListingValidator,
            _createCatalogItemWithListingValidator, _updateCatalogItemAndListingValidator,
            _deleteListingValidator, _activateListingValidator,
            _deactivateListingValidator, _updateListingStockValidator);
        return new ProductGrpcService(productHandler, listingHandler);
    }
    
    [Test]
    public async Task GetProductPrices_ShouldReturnPrices_WhenQuerySucceeds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var request = new GetProductPricesRequest { ProductIds = { id1.ToString(), id2.ToString() } };


        var prices = new List<ProductPriceDto>
        {
            new(id1, 10.00m, "USD", Guid.NewGuid(), Guid.NewGuid()),
            new(id2, 20.00m, "EUR", Guid.NewGuid(), Guid.NewGuid())
        };
        _mediator
            .Send(Arg.Any<GetProductPricesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<ProductPriceDto>>.Success(prices));

        var response = await BuildService().GetProductPrices(request, _callContext);

        Assert.That(response.Prices, Has.Count.EqualTo(2));
        Assert.That(response.Prices[0].ProductId, Is.EqualTo(id1.ToString()));
        Assert.That(response.Prices[1].Currency,  Is.EqualTo("EUR"));
    }

    [Test]
    public async Task GetProductPrices_ShouldSendCorrectIds_ToQuery()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var request = new GetProductPricesRequest { ProductIds = { id1.ToString(), id2.ToString() } };

        _mediator
            .Send(Arg.Any<GetProductPricesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<ProductPriceDto>>.Success([]));

        await BuildService().GetProductPrices(request, _callContext);

        await _mediator.Received(1).Send(
            Arg.Is<GetProductPricesQuery>(q =>
                q.ProductIds.Contains(id1) && q.ProductIds.Contains(id2)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetProductPrices_ShouldThrowRpcException_WhenQueryFails()
    {
        var request = new GetProductPricesRequest { ProductIds = { Guid.NewGuid().ToString() } };


        _mediator
            .Send(Arg.Any<GetProductPricesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<ProductPriceDto>>.Failure("DB error"));

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProductPrices(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.Internal));
    }

    [Test]
    public void GetProductPrices_ShouldThrowRpcException_WithInvalidArgument_WhenGuidFormatIsInvalid()
    {
        var request = new GetProductPricesRequest { ProductIds = { "bad-guid" } };

        // validation mocked to pass - FormatException thrown inside handler by Guid.Parse

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProductPrices(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void GetProductPrices_ShouldThrowRpcException_WithInternal_WhenUnexpectedExceptionOccurs()
    {
        var request = new GetProductPricesRequest { ProductIds = { Guid.NewGuid().ToString() } };


        _mediator
            .Send(Arg.Any<GetProductPricesQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("unexpected"));

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProductPrices(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.Internal));
        Assert.That(ex.Status.Detail, Does.Contain("GetProductPrices"));
    }

    [Test]
    public void GetProductPrices_ShouldNotWrapExistingRpcException()
    {
        var request = new GetProductPricesRequest { ProductIds = { Guid.NewGuid().ToString() } };


        var original = new RpcException(new Status(StatusCode.Unavailable, "service down"));
        _mediator
            .Send(Arg.Any<GetProductPricesQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(original);

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProductPrices(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.Unavailable));
    }
    
    [Test]
    public async Task GetProducts_ShouldReturnProducts_WhenQuerySucceeds()
    {
        var id1 = Guid.NewGuid();
        var request = new GetProductsRequest { ProductIds = { id1.ToString() } };


        var products = new List<ProductDetailDto>
        {
            SampleDetail(id1)
        };
        _mediator
            .Send(Arg.Any<GetProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<ProductDetailDto>>.Success(products));

        var response = await BuildService().GetProducts(request, _callContext);

        Assert.That(response.Products,    Has.Count.EqualTo(1));
        Assert.That(response.NotFoundIds, Is.Empty);
    }

    [Test]
    public async Task GetProducts_ShouldPopulateNotFoundIds_WhenSomeIdsNotReturned()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var request = new GetProductsRequest { ProductIds = { id1.ToString(), id2.ToString() } };


        // only id1 returned - id2 should appear in not_found_ids
        _mediator
            .Send(Arg.Any<GetProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<ProductDetailDto>>.Success([SampleDetail(id1)]));

        var response = await BuildService().GetProducts(request, _callContext);

        Assert.That(response.Products,    Has.Count.EqualTo(1));
        Assert.That(response.NotFoundIds, Has.Count.EqualTo(1));
        Assert.That(response.NotFoundIds[0], Is.EqualTo(id2.ToString()));
    }

    [Test]
    public void GetProducts_ShouldThrowRpcException_WhenValidationFails()
    {
        var request = new GetProductsRequest { ProductIds = { "bad" } };


        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProducts(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void GetProducts_ShouldThrowRpcException_WhenQueryFails()
    {
        var request = new GetProductsRequest { ProductIds = { Guid.NewGuid().ToString() } };


        _mediator
            .Send(Arg.Any<GetProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<ProductDetailDto>>.Failure("lookup error"));

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProducts(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.Internal));
    }

    [Test]
    public void GetProducts_ShouldThrowRpcException_WithInternal_WhenUnexpectedExceptionOccurs()
    {
        var request = new GetProductsRequest { ProductIds = { Guid.NewGuid().ToString() } };


        _mediator
            .Send(Arg.Any<GetProductsQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("db down"));

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProducts(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.Internal));
        Assert.That(ex.Status.Detail, Does.Contain("GetProducts"));
    }
    
    [Test]
    public async Task GetProduct_ShouldReturnProduct_WhenQuerySucceeds()
    {
        var id = Guid.NewGuid();
        var request = new GetProductRequest { ProductId = id.ToString() };


        _mediator
            .Send(Arg.Any<GetProductQuery>(), Arg.Any<CancellationToken>())
            .Returns(SampleDetail(id));

        var response = await BuildService().GetProduct(request, _callContext);

        Assert.That(response.Product.ProductId, Is.EqualTo(id.ToString()));
    }

    [Test]
    public void GetProduct_ShouldThrowRpcException_WhenGuidFormatIsInvalid()
    {
        var request = new GetProductRequest { ProductId = "not-a-guid" };
        

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProduct(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void GetProduct_ShouldThrowRpcException_WithNotFound_WhenProductNotFound()
    {
        var id = Guid.NewGuid();
        var request = new GetProductRequest { ProductId = id.ToString() };


        _mediator
            .Send(Arg.Any<GetProductQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProductNotFoundException(id));

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProduct(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.NotFound));
    }

    [Test]
    public async Task GetProduct_ShouldSendCorrectId_ToQuery()
    {
        var id = Guid.NewGuid();
        var request = new GetProductRequest { ProductId = id.ToString() };


        _mediator
            .Send(Arg.Any<GetProductQuery>(), Arg.Any<CancellationToken>())
            .Returns(SampleDetail(id));

        await BuildService().GetProduct(request, _callContext);

        await _mediator.Received(1).Send(
            Arg.Is<GetProductQuery>(q => q.ProductId == id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetProduct_ShouldThrowRpcException_WithInternal_WhenUnexpectedExceptionOccurs()
    {
        var id = Guid.NewGuid();
        var request = new GetProductRequest { ProductId = id.ToString() };


        _mediator
            .Send(Arg.Any<GetProductQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("unexpected"));

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProduct(request, _callContext));

        Assert.That(ex!.StatusCode,    Is.EqualTo(StatusCode.Internal));
        Assert.That(ex.Status.Detail,  Does.Contain("GetProduct"));
    }

    [Test]
    public void GetProduct_ShouldNotWrapExistingRpcException()
    {
        var id = Guid.NewGuid();
        var request = new GetProductRequest { ProductId = id.ToString() };


        var original = new RpcException(new Status(StatusCode.Unavailable, "service unavailable"));
        _mediator
            .Send(Arg.Any<GetProductQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(original);

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetProduct(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.Unavailable));
    }
    
    private static ProductDetailDto SampleDetail(Guid id) => new(
        ProductId: id,
        Name:"Widget",
        Description:"A test widget",
        CategoryId: Guid.NewGuid(),
        CategoryName: "Tools",
        Price: 19.99m,
        Currency: "USD",
        StockQuantity: 50,
        Status: "Approved",
        SellerId: Guid.NewGuid(),
        Attributes: [],
        ImageUrls: [],
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: null,
        CatalogItemId: Guid.NewGuid(),
        Gtin: null,
        Condition: "New",
        SellerNotes: null);

    [Test]
    public async Task GetListingsForCatalogItem_ShouldReturnListings_WhenQuerySucceeds()
    {
        var catalogItemId = Guid.NewGuid();  
        var listingId = Guid.NewGuid();
        var request = new GetListingsForCatalogItemRequest
        {
            CatalogItemId = catalogItemId.ToString(),
            Page = 1,
            Size = 20,
            SortBy = "price",
        };

        var paged = new PagedResult<ProductDetailDto>(
            Items: [SampleDetail(listingId)],
            TotalCount: 1,
            Page: 1,
            Size: 20);

        _mediator
            .Send(Arg.Any<GetListingsForCatalogItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<ProductDetailDto>>.Success(paged));

        var response = await BuildService().GetListingsForCatalogItem(request, _callContext);

        Assert.That(response.Listings, Has.Count.EqualTo(1));
        Assert.That(response.TotalCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetListingsForCatalogItem_ShouldSendCorrectCatalogItemId_ToQuery()
    {
        var catalogItemId = Guid.NewGuid();
        var request = new GetListingsForCatalogItemRequest
        {
            CatalogItemId = catalogItemId.ToString(),
            Page = 2,
            Size = 10,
            SortBy = "price",
            ConditionFilter = "New",
        };

        var paged = new PagedResult<ProductDetailDto>([], 0, 2, 10);
        _mediator
            .Send(Arg.Any<GetListingsForCatalogItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<ProductDetailDto>>.Success(paged));

        await BuildService().GetListingsForCatalogItem(request, _callContext);

        await _mediator.Received(1).Send(
            Arg.Is<GetListingsForCatalogItemQuery>(q =>
                q.CatalogItemId == catalogItemId &&
                q.Page == 2 &&
                q.Size == 10 &&
                q.SortBy == "price" &&
                q.ConditionFilter == "New"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetListingsForCatalogItem_ShouldDefaultToPage1Size20_WhenZeroProvided()
    {
        var request = new GetListingsForCatalogItemRequest
        {
            CatalogItemId = Guid.NewGuid().ToString(),
            Page = 0,
            Size = 0,
        };

        var paged = new PagedResult<ProductDetailDto>([], 0, 1, 20);
        _mediator
            .Send(Arg.Any<GetListingsForCatalogItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<ProductDetailDto>>.Success(paged));

        await BuildService().GetListingsForCatalogItem(request, _callContext);

        await _mediator.Received(1).Send(
            Arg.Is<GetListingsForCatalogItemQuery>(q => q.Page == 1 && q.Size == 20),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void GetListingsForCatalogItem_ShouldThrowRpcException_WhenGuidFormatIsInvalid()
    {
        var request = new GetListingsForCatalogItemRequest { CatalogItemId = "not-a-guid" };

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetListingsForCatalogItem(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void GetListingsForCatalogItem_ShouldThrowRpcException_WhenQueryFails()
    {
        var request = new GetListingsForCatalogItemRequest
        {
            CatalogItemId = Guid.NewGuid().ToString(),
            Page = 1,
            Size = 10,
        };

        _mediator
            .Send(Arg.Any<GetListingsForCatalogItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<ProductDetailDto>>.Failure("repository error"));

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().GetListingsForCatalogItem(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.Internal));
    }
}
