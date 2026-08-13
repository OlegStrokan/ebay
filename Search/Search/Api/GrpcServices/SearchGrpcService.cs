using Application.Gateways;
using Application.Queries.GetFrequentlyBoughtTogether;
using Application.Queries.GetSimilarItems;
using Application.Queries.SearchProducts;
using Domain.Common.Interfaces;
using Grpc.Core;
using Protos.Common;
using Protos.Search;

namespace Api.GrpcServices;

public sealed class SearchGrpcService(
    IQueryHandler<SearchProductsQuery, SearchProductsResult> handler,
    IQueryHandler<GetSimilarItemsQuery, GetSimilarItemsResult> similarItemsHandler,
    IQueryHandler<GetFrequentlyBoughtTogetherQuery, GetFrequentlyBoughtTogetherResult> frequentlyBoughtTogetherHandler,
    IAiSearchStreamGateway streamGateway,
    ILogger<SearchGrpcService> logger)
    : SearchService.SearchServiceBase
{
    public override async Task<SearchResponse> Search(
        SearchRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Query cannot be empty."));
        }

        if (request.Query.Length > 500)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Query cannot exceed 500 characters."));
        }

        if (request.Page < 1)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Page must be >= 1."));
        }

        if (request.PageSize is < 1 or > 100)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "PageSize must be between 1 and 100."));
        }

        var query = new SearchProductsQuery(
            QueryText: request.Query,
            UseAi: request.UseAi,
            Page: request.Page,
            Size: request.PageSize,
            UserId: string.IsNullOrEmpty(request.UserId) ? null : request.UserId);

        var result = await handler.HandleAsync(query, context.CancellationToken);

        var response = new SearchResponse
        {
            TotalCount = result.TotalCount,
            Page = result.Page,
            Size = result.Size,
            WasAiSearch = result.WasAiSearch,
            ParsedQueryDebug = result.ParsedQueryDebug ?? string.Empty
        };

        response.Items.AddRange(result.Items.Select(i =>
        {
            var item = new SearchResultItem
            {
                ProductId = i.ProductId.ToString(),
                Name = i.Name,
                Category = i.Category,
                Price = DecimalValueMapper.ToProto(i.Price),
                Currency = i.Currency,
                RelevanceScore = i.RelevanceScore
            };
            item.ImageUrls.AddRange(i.ImageUrls);
            return item;
        }));

        logger.LogDebug(
            "Search request processed for query [{Query}]. Returned {Count} items.",
            request.Query,
            result.Items.Count);

        return response;
    }

    public override async Task StreamSearch(
        StreamSearchRequest request,
        IServerStreamWriter<StreamSearchResponse> responseStream,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Query cannot be empty."));

        if (request.Query.Length > 500)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Query cannot exceed 500 characters."));

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 100 ? 20 : request.PageSize;
        var userId = string.IsNullOrEmpty(request.UserId) ? null : request.UserId;
        var ct = context.CancellationToken;

        await foreach (var partial in streamGateway
                           .SearchStreamAsync(request.Query, page, pageSize, userId, ct)
                           .WithCancellation(ct))
        {
            var phase = partial.Phase switch
            {
                SearchResultPhase.Keyword => SearchPhase.Keyword,
                SearchResultPhase.Merged  => SearchPhase.Merged,
                _ => SearchPhase.Keyword
            };

            var msg = new StreamSearchResponse
            {
                Phase = phase,
                TotalCount = partial.TotalCount,
                WasAiSearch = partial.WasAiSearch
            };

            msg.Items.AddRange(partial.Items.Select(i =>
            {
                var item = new SearchResultItem
                {
                    ProductId = i.ProductId.ToString(),
                    Name = i.Name,
                    Category = i.Category,
                    Price = DecimalValueMapper.ToProto(i.Price),
                    Currency = i.Currency,
                    RelevanceScore = i.RelevanceScore
                };
                item.ImageUrls.AddRange(i.ImageUrls);
                return item;
            }));

            await responseStream.WriteAsync(msg, ct);

            logger.LogDebug(
                "Streamed {Phase} results for query [{Query}]: {Count} items.",
                partial.Phase, request.Query, partial.Items.Count);
        }
    }

    public override async Task<GetSimilarItemsResponse> GetSimilarItems(
        GetSimilarItemsRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.CatalogItemId))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "catalog_item_id is required."));
        }

        var limit = request.Limit is < 1 or > 50 ? 10 : request.Limit;

        var query = new GetSimilarItemsQuery(
            CatalogItemId: request.CatalogItemId,
            Limit: limit,
            Category: string.IsNullOrEmpty(request.Category) ? null : request.Category,
            Condition: string.IsNullOrEmpty(request.Condition) ? null : request.Condition);

        var result = await similarItemsHandler.HandleAsync(query, context.CancellationToken);

        var response = new GetSimilarItemsResponse();
        response.Items.AddRange(result.Items.Select(i => new SimilarItem
        {
            CatalogItemId = i.CatalogItemId,
            Score = i.Score
        }));

        logger.LogDebug(
            "GetSimilarItems returned {Count} items for [{CatalogItemId}].",
            result.Items.Count,
            request.CatalogItemId);

        return response;
    }

    public override async Task<GetFrequentlyBoughtTogetherResponse> GetFrequentlyBoughtTogether(
        GetFrequentlyBoughtTogetherRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.CatalogItemId))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "catalog_item_id is required."));
        }

        var limit = request.Limit is < 1 or > 50 ? 10 : request.Limit;

        var query = new GetFrequentlyBoughtTogetherQuery(
            CatalogItemId: request.CatalogItemId,
            Limit: limit);

        var result = await frequentlyBoughtTogetherHandler.HandleAsync(query, context.CancellationToken);

        var response = new GetFrequentlyBoughtTogetherResponse();
        response.Items.AddRange(result.Items.Select(i => new CoOccurrenceItem
        {
            CatalogItemId = i.CatalogItemId,
            Score = i.Score
        }));

        logger.LogDebug(
            "GetFrequentlyBoughtTogether returned {Count} items for [{CatalogItemId}].",
            result.Items.Count,
            request.CatalogItemId);

        return response;
    }
}