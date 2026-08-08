using Application.Queries.SearchProducts;
using Elastic.Clients.Elasticsearch.Mapping;
using FluentAssertions;
using Infrastructure.ElasticSearch;
using Infrastructure.ElasticSearch.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Search.IntegrationTests.Infrastructure;
using Xunit;

namespace Search.IntegrationTests.ElasticSearch;

[Collection("Elasticsearch")]
public sealed class ElasticsearchSearcherTests
{
    private readonly ElasticsearchFixture _fixture;

    public ElasticsearchSearcherTests(ElasticsearchFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SearchAsync_WhenDocumentIndexed_ShouldReturnMappedResult()
    {
        await RecreateProductsIndexAsync();

        var productId = Guid.NewGuid();
        var document = new ProductSearchDocument
        {
            Id = productId.ToString(),
            Name = "Ultra",
            Description = "Ultra performance computer",
            CategoryId = "Computers",
            Price = 1999.99,
            Currency = "USD",
            Stock = 3,
            Attributes = new Dictionary<string, string>
            {
                ["color"] = "black",
                ["layout"] = "qwerty",
                ["brand"] = "Contoso"
            },
            ImageUrls = ["https://img.test/laptop-1.png"],
            ProductType = "catalog_item",
        };

        var indexResponse = await _fixture.Client.IndexAsync(
            document,
            ElasticsearchIndexInitializer.IndexName,
            i => i.Id(document.Id));

        indexResponse.IsValidResponse.Should().BeTrue();

        await _fixture.Client.Indices.RefreshAsync(ElasticsearchIndexInitializer.IndexName);

        var searcher = new ElasticsearchSearcher(
            _fixture.Client,
            NullLogger<ElasticsearchSearcher>.Instance);

        // "Ultra" hits the name field. The searcher queries name/description/attributes only —
        // categoryId is a keyword that is filtered on, never full-text searched.
        var result = await searcher.SearchAsync(
            new SearchProductsQuery("Ultra", UseAi: false, Page: 1, Size: 10),
            CancellationToken.None);

        result.WasAiSearch.Should().BeFalse();
        result.Page.Should().Be(1);
        result.Size.Should().Be(10);
        result.Items.Should().NotBeEmpty();
        result.TotalCount.Should().BeGreaterThanOrEqualTo(1);

        var item = result.Items.Single(i => i.ProductId == productId);
        item.Name.Should().Be("Ultra");
        item.Category.Should().Be("Computers");
        item.Currency.Should().Be("USD");
        item.Price.Should().Be(1999.99m);
        item.ImageUrls.Should().ContainSingle("https://img.test/laptop-1.png");
    }

    [Fact]
    public async Task SearchAsync_WhenIndexMissing_ShouldReturnEmptyResult()
    {
        await DeleteProductsIndexIfExistsAsync();

        var searcher = new ElasticsearchSearcher(
            _fixture.Client,
            NullLogger<ElasticsearchSearcher>.Instance);

        var result = await searcher.SearchAsync(
            new SearchProductsQuery("anything", UseAi: false, Page: 1, Size: 10),
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.WasAiSearch.Should().BeFalse();
    }

    private async Task RecreateProductsIndexAsync()
    {
        await DeleteProductsIndexIfExistsAsync();

        // Catalog owns the index schema; replicate it here for testing.
        await _fixture.Client.Indices.CreateAsync(
            ElasticsearchIndexInitializer.IndexName,
            c => c.Mappings(m => m.Properties(new Properties
            {
                { "name",        new TextProperty { Boost = 3.0 } },
                { "description", new TextProperty { Boost = 1.5 } },
                { "categoryId",  new KeywordProperty() },
                { "price",       new FloatNumberProperty() },
                { "currency",    new KeywordProperty() },
                { "stock",       new IntegerNumberProperty() },
                { "attributes",  new FlattenedProperty() },
                { "imageUrls",   new KeywordProperty { Index = false } },
                { "createdAt",   new DateProperty() },
                { "updatedAt",   new DateProperty() },
                { "productType", new KeywordProperty() }
            })));
    }

    private async Task DeleteProductsIndexIfExistsAsync()
    {
        var exists = await _fixture.Client.Indices.ExistsAsync(ElasticsearchIndexInitializer.IndexName);

        if (exists.ApiCallDetails.HttpStatusCode == 200)
            await _fixture.Client.Indices.DeleteAsync(ElasticsearchIndexInitializer.IndexName);
    }
}
