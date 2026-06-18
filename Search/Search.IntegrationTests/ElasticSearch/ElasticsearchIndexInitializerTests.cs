using FluentAssertions;
using Infrastructure.ElasticSearch;
using Microsoft.Extensions.Logging.Abstractions;
using Search.IntegrationTests.Infrastructure;
using Xunit;

namespace Search.IntegrationTests.ElasticSearch;

[Collection("Elasticsearch")]
public sealed class ElasticsearchIndexInitializerTests
{
    private readonly ElasticsearchFixture _fixture;

    public ElasticsearchIndexInitializerTests(ElasticsearchFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureIndexAsync_WhenIndexMissing_ShouldNotCreateIndex()
    {
        // Search is read-only — index creation is Catalog's responsibility.
        await DeleteProductsIndexIfExistsAsync();

        var initializer = new ElasticsearchIndexInitializer(
            _fixture.Client,
            NullLogger<ElasticsearchIndexInitializer>.Instance);

        var act = () => initializer.EnsureIndexAsync();

        await act.Should().NotThrowAsync("missing index should only log a warning, not throw");

        var exists = await _fixture.Client.Indices.ExistsAsync(ElasticsearchIndexInitializer.IndexName);
        (exists.ApiCallDetails.HttpStatusCode == 200)
            .Should()
            .BeFalse("Search service must not create the index — that is Catalog's job");
    }

    [Fact]
    public async Task EnsureIndexAsync_WhenIndexExists_ShouldCompleteWithoutError()
    {
        await DeleteProductsIndexIfExistsAsync();
        await _fixture.Client.Indices.CreateAsync(ElasticsearchIndexInitializer.IndexName);

        var initializer = new ElasticsearchIndexInitializer(
            _fixture.Client,
            NullLogger<ElasticsearchIndexInitializer>.Instance);

        var act = () => initializer.EnsureIndexAsync();

        await act.Should().NotThrowAsync("existing index should be a no-op");

        var exists = await _fixture.Client.Indices.ExistsAsync(ElasticsearchIndexInitializer.IndexName);
        (exists.ApiCallDetails.HttpStatusCode == 200)
            .Should()
            .BeTrue("the pre-existing index should remain untouched");
    }

    [Fact]
    public async Task EnsureIndexAsync_ShouldBeIdempotent_WhenCalledMultipleTimes()
    {
        await DeleteProductsIndexIfExistsAsync();
        await _fixture.Client.Indices.CreateAsync(ElasticsearchIndexInitializer.IndexName);

        var initializer = new ElasticsearchIndexInitializer(
            _fixture.Client,
            NullLogger<ElasticsearchIndexInitializer>.Instance);

        var act = async () =>
        {
            await initializer.EnsureIndexAsync();
            await initializer.EnsureIndexAsync();
        };

        await act.Should().NotThrowAsync("repeated calls against an existing index must be a no-op");
    }

    private async Task DeleteProductsIndexIfExistsAsync()
    {
        var exists = await _fixture.Client.Indices.ExistsAsync(ElasticsearchIndexInitializer.IndexName);

        if (exists.ApiCallDetails.HttpStatusCode == 200)
            await _fixture.Client.Indices.DeleteAsync(ElasticsearchIndexInitializer.IndexName);
    }
}
