using Application.Gateways;
using Application.Queries.SearchProducts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Queries;

[TestFixture]
public sealed class SearchProductsQueryHandlerTests
{
    private IElasticsearchSearcher _elasticsearchSearcher = null!;
    private IAiSearchGateway _aiGateway = null!;
    private ILogger<SearchProductsQueryHandler> _logger = null!;
    private SearchProductsQueryHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _elasticsearchSearcher = Substitute.For<IElasticsearchSearcher>();
        _aiGateway = Substitute.For<IAiSearchGateway>();
        _logger = Substitute.For<ILogger<SearchProductsQueryHandler>>();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        _sut = new SearchProductsQueryHandler(_elasticsearchSearcher, _aiGateway, configuration, _logger);
    }

    [Test]
    public async Task HandleAsync_UseAiFalse_ShouldUseElasticsearchOnly()
    {
        var query = new SearchProductsQuery("phone", UseAi: false, Page: 1, Size: 20);
        var expected = BuildResult(wasAi: false, totalCount: 1);

        _elasticsearchSearcher
            .SearchAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.HandleAsync(query, CancellationToken.None);

        Assert.That(result, Is.EqualTo(expected));
        _ = _aiGateway.Received(0).SearchAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>());
        _ = _elasticsearchSearcher.Received(1).SearchAsync(query, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_UseAiTrue_WhenAiSucceeds_ShouldNotFallback()
    {
        var query = new SearchProductsQuery("laptop", UseAi: true, Page: 1, Size: 10);
        var aiResult = BuildResult(wasAi: true, totalCount: 2);

        _aiGateway
            .SearchAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(aiResult);

        var result = await _sut.HandleAsync(query, CancellationToken.None);

        Assert.That(result, Is.EqualTo(aiResult));
        _ = _aiGateway.Received(1).SearchAsync(query, Arg.Any<CancellationToken>());
        _ = _elasticsearchSearcher.Received(0).SearchAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_UseAiTrue_WhenAiThrows_ShouldFallbackToElasticsearch()
    {
        var query = new SearchProductsQuery("headphones", UseAi: true, Page: 2, Size: 5);
        var fallback = BuildResult(wasAi: false, totalCount: 3);

        _aiGateway
            .SearchAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("AI unavailable"));

        _elasticsearchSearcher
            .SearchAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(fallback);

        var result = await _sut.HandleAsync(query, CancellationToken.None);

        Assert.That(result, Is.EqualTo(fallback));
        _ = _aiGateway.Received(1).SearchAsync(query, Arg.Any<CancellationToken>());
        _ = _elasticsearchSearcher.Received(1).SearchAsync(query, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_UseAiTrue_WhenAiTimesOut_ShouldFallbackToElasticsearch()
    {
        var query = new SearchProductsQuery("gaming chair", UseAi: true, Page: 1, Size: 10);
        var fallback = BuildResult(wasAi: false, totalCount: 1);
        var neverCompletes = new TaskCompletionSource<SearchProductsResult>();

        _aiGateway
            .SearchAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(neverCompletes.Task);

        _elasticsearchSearcher
            .SearchAsync(Arg.Any<SearchProductsQuery>(), Arg.Any<CancellationToken>())
            .Returns(fallback);

        var result = await _sut.HandleAsync(query, CancellationToken.None);

        Assert.That(result, Is.EqualTo(fallback));
        _ = _aiGateway.Received(1).SearchAsync(query, Arg.Any<CancellationToken>());
        _ = _elasticsearchSearcher.Received(1).SearchAsync(query, Arg.Any<CancellationToken>());
    }

    private static SearchProductsResult BuildResult(bool wasAi, int totalCount)
    {
        return new SearchProductsResult(
            Items:
            [
                new ProductSearchItem(
                    ProductId: Guid.NewGuid(),
                    Name: "Item",
                    Category: "General",
                    Price: 15.5m,
                    Currency: "USD",
                    RelevanceScore: 1.0,
                    ImageUrls: ["https://img.test/1.jpg"])
            ],
            TotalCount: totalCount,
            Page: 1,
            Size: 10,
            WasAiSearch: wasAi,
            ParsedQueryDebug: wasAi ? "intent:shopping" : null);
    }
}
