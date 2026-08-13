using Api.GrpcServices;
using Application.Gateways;
using Application.Queries.GetFrequentlyBoughtTogether;
using Application.Queries.GetSimilarItems;
using Application.Queries.SearchProducts;
using Domain.Common.Interfaces;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Protos.Search;

namespace Api.Tests.GrpcServices;

[TestFixture]
public sealed class SearchGrpcServiceTests
{
    private IQueryHandler<SearchProductsQuery, SearchProductsResult> _handler = null!;
    private IQueryHandler<GetSimilarItemsQuery, GetSimilarItemsResult> _similarHandler = null!;
    private IQueryHandler<GetFrequentlyBoughtTogetherQuery, GetFrequentlyBoughtTogetherResult> _fbtHandler = null!;
    private IAiSearchStreamGateway _streamGateway = null!;
    private ILogger<SearchGrpcService> _logger = null!;
    private ServerCallContext _callContext = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = Substitute.For<IQueryHandler<SearchProductsQuery, SearchProductsResult>>();
        _similarHandler = Substitute.For<IQueryHandler<GetSimilarItemsQuery, GetSimilarItemsResult>>();
        _fbtHandler = Substitute.For<IQueryHandler<GetFrequentlyBoughtTogetherQuery, GetFrequentlyBoughtTogetherResult>>();
        _streamGateway = Substitute.For<IAiSearchStreamGateway>();
        _logger = Substitute.For<ILogger<SearchGrpcService>>();
        _callContext = Substitute.For<ServerCallContext>();
    }

    private SearchGrpcService BuildService() => new(_handler, _similarHandler, _fbtHandler, _streamGateway, _logger);

    [Test]
    public async Task Search_ShouldMapRequestAndResponse_WhenQuerySucceeds()
    {
        var request = new SearchRequest
        {
            Query = "laptop",
            UseAi = true,
            Page = 2,
            PageSize = 20
        };

        var result = new SearchProductsResult(
            Items:
            [
                new ProductSearchItem(
                    ProductId: Guid.NewGuid(),
                    Name: "Gaming Laptop",
                    Category: "Computers",
                    Price: 1299.99m,
                    Currency: "USD",
                    RelevanceScore: 3.14,
                    ImageUrls: ["https://img.test/laptop-1.png"])
            ],
            TotalCount: 47,
            Page: 2,
            Size: 20,
            WasAiSearch: true,
            ParsedQueryDebug: "intent:laptop");

        _handler
            .HandleAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var response = await BuildService().Search(request, _callContext);

        Assert.That(response.TotalCount, Is.EqualTo(47));
        Assert.That(response.Page, Is.EqualTo(2));
        Assert.That(response.Size, Is.EqualTo(20));
        Assert.That(response.WasAiSearch, Is.True);
        Assert.That(response.ParsedQueryDebug, Is.EqualTo("intent:laptop"));

        Assert.That(response.Items, Has.Count.EqualTo(1));
        Assert.That(response.Items[0].Name, Is.EqualTo("Gaming Laptop"));
        Assert.That(Protos.Common.DecimalValueMapper.ToDecimal(response.Items[0].Price), Is.EqualTo(1299.99m));
        Assert.That(response.Items[0].ImageUrls, Has.Count.EqualTo(1));

        await _handler.Received(1).HandleAsync(
            Arg.Is<SearchProductsQuery>(q =>
                q.QueryText == "laptop" &&
                q.UseAi &&
                q.Page == 2 &&
                q.Size == 20),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Search_ShouldReturnEmptyParsedDebug_WhenApplicationReturnsNull()
    {
        var request = ValidRequest();
        var result = new SearchProductsResult(
            Items: [],
            TotalCount: 0,
            Page: 1,
            Size: 10,
            WasAiSearch: false,
            ParsedQueryDebug: null);

        _handler
            .HandleAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var response = await BuildService().Search(request, _callContext);

        Assert.That(response.ParsedQueryDebug, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Search_WhenQueryIsEmpty_ShouldThrowInvalidArgument()
    {
        var request = ValidRequest();
        request.Query = "   ";

        var ex = Assert.ThrowsAsync<RpcException>(() => BuildService().Search(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(ex.Status.Detail, Does.Contain("Query cannot be empty"));
        _ = _handler.DidNotReceive().HandleAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Search_WhenPageIsLessThanOne_ShouldThrowInvalidArgument()
    {
        var request = ValidRequest();
        request.Page = 0;

        var ex = Assert.ThrowsAsync<RpcException>(() => BuildService().Search(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(ex.Status.Detail, Does.Contain("Page must be >= 1"));
        _ = _handler.DidNotReceive().HandleAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Search_WhenPageSizeIsOutOfRange_ShouldThrowInvalidArgument()
    {
        var request = ValidRequest();
        request.PageSize = 101;

        var ex = Assert.ThrowsAsync<RpcException>(() => BuildService().Search(request, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(ex.Status.Detail, Does.Contain("PageSize must be between 1 and 100"));
        _ = _handler.DidNotReceive().HandleAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>());
    }

    private static SearchRequest ValidRequest() => new()
    {
        Query = "phone",
        UseAi = false,
        Page = 1,
        PageSize = 10
    };

    [Test]
    public async Task Search_ShouldForwardUserId_WhenProvided()
    {
        var request = ValidRequest();
        request.UserId = "user-42";

        var result = new SearchProductsResult(
            Items: [],
            TotalCount: 0,
            Page: 1,
            Size: 10,
            WasAiSearch: false,
            ParsedQueryDebug: null);

        _handler
            .HandleAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(result);

        await BuildService().Search(request, _callContext);

        await _handler.Received(1).HandleAsync(
            Arg.Is<SearchProductsQuery>(q => q.UserId == "user-42"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Search_ShouldForwardNullUserId_WhenEmpty()
    {
        var request = ValidRequest();
        request.UserId = "";

        var result = new SearchProductsResult(
            Items: [],
            TotalCount: 0,
            Page: 1,
            Size: 10,
            WasAiSearch: false,
            ParsedQueryDebug: null);

        _handler
            .HandleAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(result);

        await BuildService().Search(request, _callContext);

        await _handler.Received(1).HandleAsync(
            Arg.Is<SearchProductsQuery>(q => q.UserId == null),
            Arg.Any<CancellationToken>());
    }
}

[TestFixture]
public sealed class SearchGrpcServiceStreamTests
{
    private IQueryHandler<SearchProductsQuery, SearchProductsResult> _handler = null!;
    private IQueryHandler<GetSimilarItemsQuery, GetSimilarItemsResult> _similarHandler = null!;
    private IQueryHandler<GetFrequentlyBoughtTogetherQuery, GetFrequentlyBoughtTogetherResult> _fbtHandler = null!;
    private IAiSearchStreamGateway _streamGateway = null!;
    private ILogger<SearchGrpcService> _logger = null!;
    private ServerCallContext _callContext = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = Substitute.For<IQueryHandler<SearchProductsQuery, SearchProductsResult>>();
        _similarHandler = Substitute.For<IQueryHandler<GetSimilarItemsQuery, GetSimilarItemsResult>>();
        _fbtHandler = Substitute.For<IQueryHandler<GetFrequentlyBoughtTogetherQuery, GetFrequentlyBoughtTogetherResult>>();
        _streamGateway = Substitute.For<IAiSearchStreamGateway>();
        _logger = Substitute.For<ILogger<SearchGrpcService>>();
        _callContext = Substitute.For<ServerCallContext>();
        _callContext.CancellationToken.Returns(CancellationToken.None);
    }

    private SearchGrpcService BuildService() => new(_handler, _similarHandler, _fbtHandler, _streamGateway, _logger);

    [Test]
    public void StreamSearch_WhenQueryIsEmpty_ShouldThrowInvalidArgument()
    {
        var request = new StreamSearchRequest { Query = "   ", Page = 1, PageSize = 20 };
        var writer = Substitute.For<IServerStreamWriter<StreamSearchResponse>>();

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().StreamSearch(request, writer, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void StreamSearch_WhenQueryExceeds500Chars_ShouldThrowInvalidArgument()
    {
        var request = new StreamSearchRequest { Query = new string('a', 501), Page = 1, PageSize = 20 };
        var writer = Substitute.For<IServerStreamWriter<StreamSearchResponse>>();

        var ex = Assert.ThrowsAsync<RpcException>(() =>
            BuildService().StreamSearch(request, writer, _callContext));

        Assert.That(ex!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public async Task StreamSearch_ShouldWriteTwoPhases_WhenGatewayYieldsBoth()
    {
        var request = new StreamSearchRequest { Query = "laptop", Page = 1, PageSize = 10 };
        var writer = Substitute.For<IServerStreamWriter<StreamSearchResponse>>();

        var productId = Guid.NewGuid();
        var streamResults = new List<StreamSearchResult>
        {
            new(
                Items: [new ProductSearchItem(productId, "Laptop", "Electronics", 999m, "USD", 0.9, [])],
                TotalCount: 1,
                WasAiSearch: true,
                Phase: SearchResultPhase.Keyword),
            new(
                Items: [new ProductSearchItem(productId, "Laptop", "Electronics", 999m, "USD", 0.95, [])],
                TotalCount: 1,
                WasAiSearch: true,
                Phase: SearchResultPhase.Merged),
        };

        _streamGateway
            .SearchStreamAsync("laptop", 1, 10, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(streamResults.ToAsyncEnumerable());

        await BuildService().StreamSearch(request, writer, _callContext);

        await writer.Received(2).WriteAsync(Arg.Any<StreamSearchResponse>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StreamSearch_ShouldMapPhaseCorrectly()
    {
        var request = new StreamSearchRequest { Query = "phone", Page = 1, PageSize = 20 };
        var writer = Substitute.For<IServerStreamWriter<StreamSearchResponse>>();

        var streamResults = new List<StreamSearchResult>
        {
            new(Items: [], TotalCount: 0, WasAiSearch: false, Phase: SearchResultPhase.Keyword),
            new(Items: [], TotalCount: 0, WasAiSearch: false, Phase: SearchResultPhase.Merged),
        };

        _streamGateway
            .SearchStreamAsync("phone", 1, 20, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(streamResults.ToAsyncEnumerable());

        var written = new List<StreamSearchResponse>();
        await writer.WriteAsync(Arg.Do<StreamSearchResponse>(r => written.Add(r)), Arg.Any<CancellationToken>());

        await BuildService().StreamSearch(request, writer, _callContext);

        Assert.That(written[0].Phase, Is.EqualTo(SearchPhase.Keyword));
        Assert.That(written[1].Phase, Is.EqualTo(SearchPhase.Merged));
    }

    [Test]
    public async Task StreamSearch_ShouldDefaultPageAndSize_WhenInvalid()
    {
        var request = new StreamSearchRequest { Query = "test", Page = 0, PageSize = 0 };
        var writer = Substitute.For<IServerStreamWriter<StreamSearchResponse>>();

        _streamGateway
            .SearchStreamAsync("test", 1, 20, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<StreamSearchResult>());

        await BuildService().StreamSearch(request, writer, _callContext);

        _streamGateway.Received(1).SearchStreamAsync("test", 1, 20, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StreamSearch_ShouldForwardUserId_WhenProvided()
    {
        var request = new StreamSearchRequest { Query = "test", Page = 1, PageSize = 10, UserId = "user-99" };
        var writer = Substitute.For<IServerStreamWriter<StreamSearchResponse>>();

        _streamGateway
            .SearchStreamAsync("test", 1, 10, "user-99", Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<StreamSearchResult>());

        await BuildService().StreamSearch(request, writer, _callContext);

        _streamGateway.Received(1).SearchStreamAsync("test", 1, 10, "user-99", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StreamSearch_ShouldForwardNullUserId_WhenEmpty()
    {
        var request = new StreamSearchRequest { Query = "test", Page = 1, PageSize = 10, UserId = "" };
        var writer = Substitute.For<IServerStreamWriter<StreamSearchResponse>>();

        _streamGateway
            .SearchStreamAsync("test", 1, 10, null, Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<StreamSearchResult>());

        await BuildService().StreamSearch(request, writer, _callContext);

        _streamGateway.Received(1).SearchStreamAsync("test", 1, 10, null, Arg.Any<CancellationToken>());
    }
}
