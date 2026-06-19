using Application.Gateways;
using Infrastructure.ElasticSearch.Documents;
using Application.Queries.SearchProducts;

namespace Infrastructure.ElasticSearch;

using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;

public sealed class ElasticsearchSearcher : IElasticsearchSearcher
{
    private readonly ElasticsearchClient _client;
    private readonly ILogger<ElasticsearchSearcher> _logger;

    public ElasticsearchSearcher(
        ElasticsearchClient client,
        ILogger<ElasticsearchSearcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<SearchProductsResult> SearchAsync(
        SearchProductsQuery query,
        CancellationToken   ct)
    {
        var from = (query.Page - 1) * query.Size;

        var response = await _client
            .SearchAsync<ProductSearchDocument>(s => s
                .Index(ElasticsearchIndexInitializer.IndexName)
                .From(from)
                .Size(query.Size)
                .Query(q => q
                    .Bool(b => b
                        .Filter(f => f
                            .Term(t => t.Field("productType").Value(query.ProductType))
                        )
                        .Must(m => m
                            .MultiMatch(mm => mm
                                .Query(query.QueryText)
                                .Fields(new Field[]
                                {
                                    new("name", 3.0),
                                    new("description"),
                                    new("attributes")
                                })
                                .Type(TextQueryType.BestFields)
                            )
                        )
                    )
                ), ct);


        if (!response.IsValidResponse)
        {
            _logger.LogError(
                "Elasticsearch query failed for [{Query}]: {Debug}",
                query.QueryText,
                response.DebugInformation);

            return new SearchProductsResult(
                Items: [],
                TotalCount: 0,
                Page: query.Page,
                Size: query.Size,
                WasAiSearch: false,
                ParsedQueryDebug: null);
        }

        var items = response.Hits
            .Where(h => h.Source is not null)
            .Select(h => new ProductSearchItem(
                ProductId: Guid.Parse(h.Source!.Id),
                Name: h.Source.Name,
                Category: h.Source.CategoryId,
                Price: (decimal)h.Source.Price,
                Currency: h.Source.Currency,
                RelevanceScore: h.Score ?? 0d,
                ImageUrls: h.Source.ImageUrls))
            .ToList();

        var totalCount = response.HitsMetadata?.Total is { } total
            ? total.Match(
                totalHits => (int)totalHits.Value,
                value => (int)value)
            : items.Count;

        return new SearchProductsResult(
            Items: items,
            TotalCount: totalCount,
            Page: query.Page,
            Size: query.Size,
            WasAiSearch: false,
            ParsedQueryDebug: null);
    }
}