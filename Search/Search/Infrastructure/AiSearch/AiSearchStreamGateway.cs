using System.Runtime.CompilerServices;
using Application.Gateways;
using Application.Queries.SearchProducts;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Protos.AiSearch;
using Protos.Common;

namespace Infrastructure.AiSearch;

// Opens a bidirectional gRPC stream per request.
// Sends one query, yields two phases (keyword then merged), closes.
// Cancellation (client disconnect) propagates to server and kills in-flight Qdrant work.
public sealed class AiSearchStreamGateway(
    AiSearchService.AiSearchServiceClient client,
    ILogger<AiSearchStreamGateway> logger) : IAiSearchStreamGateway
{
    public async IAsyncEnumerable<StreamSearchResult> SearchStreamAsync(
        string query, int page, int size, string? userId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");

        using var call = client.SearchStream(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new AiStreamSearchRequest
        {
            RequestId = requestId,
            Query = query,
            Page = page,
            PageSize = size,
            UserId = userId ?? string.Empty
        }, ct);

        await call.RequestStream.CompleteAsync();

        logger.LogDebug("Opened stream for query [{Query}] request_id={RequestId}",
            query, requestId);

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            var items = response.Items
                .Select(i => new ProductSearchItem(
                    ProductId: Guid.Parse(i.ProductId),
                    Name: i.Name,
                    Category: i.Category,
                    Price: DecimalValueMapper.ToDecimal(i.Price),
                    Currency: i.Currency,
                    RelevanceScore: i.RelevanceScore,
                    ImageUrls: i.ImageUrls.ToList()))
                .ToList();

            yield return new StreamSearchResult(
                Items: items,
                TotalCount: response.TotalCount,
                WasAiSearch: response.UsedAi,
                Phase: response.Phase switch
                {
                    AiSearchPhase.SearchPhaseKeyword => SearchResultPhase.Keyword,
                    AiSearchPhase.SearchPhaseMerged => SearchResultPhase.Merged,
                    _ => SearchResultPhase.Keyword
                }
            );
        }
    }
}
