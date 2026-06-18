using Application.Gateways;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Domain.Common.Interfaces;

namespace Application.Queries.SearchProducts;

public sealed class SearchProductsQueryHandler(
    IElasticsearchSearcher elasticsearchSearcher,
    IAiSearchGateway aiGateway,
    IConfiguration configuration,
    ILogger<SearchProductsQueryHandler> logger)
: IQueryHandler<SearchProductsQuery, SearchProductsResult>
{
    private TimeSpan AiTimeout =>
        TimeSpan.FromMilliseconds(configuration.GetValue<int>("AiSearch:TimeoutMs", 500));

    public async Task<SearchProductsResult> HandleAsync(SearchProductsQuery query, CancellationToken ct)
    {
        if (query.UseAi)
        {
            try
            {
                return await aiGateway
                    .SearchAsync(query, ct)
                    .WaitAsync(AiTimeout, ct);
            }
            catch (TimeoutException)
            {
                logger.LogWarning(
                    "AI search timed out after {Ms}ms for query [{Query}]. " +
                    "Falling back to Elasticsearch.",
                    AiTimeout.TotalMilliseconds,
                    query.QueryText);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "AI search threw an exception for query [{Query}]. " +
                    "Falling back to Elasticsearch.",
                    query.QueryText);
            }
        }

        return await elasticsearchSearcher.SearchAsync(query, ct);
    }
}