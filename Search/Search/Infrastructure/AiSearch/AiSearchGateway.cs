using Application.Gateways;
using Application.Queries.SearchProducts;
using Microsoft.Extensions.Logging;
using Protos.AiSearch;
using Protos.Common;

namespace Infrastructure.AiSearch;

// registered when AiSearch:Enabled = true
public class AiSearchGateway(
    AiSearchService.AiSearchServiceClient _grpcClient,
    ILogger<AiSearchGateway> _logger
    ) : IAiSearchGateway
{
    public async Task<SearchProductsResult> SearchAsync(SearchProductsQuery query, CancellationToken ct)
    {
        var request = new AiSearchRequest
        {
            Query = query.QueryText,
            Page = query.Page,
            PageSize = query.Size,
            Debug = false,
            UserId = query.UserId ?? string.Empty
        };


        var response = await _grpcClient.SearchAsync(
            request, cancellationToken: ct);

        var items = response.Items
            .Select(i => new ProductSearchItem(
                ProductId: Guid.Parse(i.ProductId),
                Name: i.Name,
                Category: i.Category,
                Price: DecimalValueMapper.ToDecimal(i.Price),
                Currency: i.Currency,
                RelevanceScore: i.RelevanceScore,
                ImageUrls: i.ImageUrls.ToList())
            ).ToList();

        _logger.LogDebug(
            "AI search returned {Count} items for query [{Query}] on page {Page}.",
            items.Count,
            query.QueryText,
            query.Page);

        return new SearchProductsResult(
            Items: items,
            TotalCount: response.TotalCount,
            Page: query.Page,
            Size: query.Size,
            WasAiSearch: response.UsedAi,
            ParsedQueryDebug: response.ParsedQueryDebug
        );
    }
}