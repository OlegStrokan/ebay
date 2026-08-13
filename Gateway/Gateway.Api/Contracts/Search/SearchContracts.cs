namespace Gateway.Api.Contracts.Search;

public sealed record SearchResponse(
    IReadOnlyList<SearchResultItemResponse> Items,
    int TotalCount,
    int Page,
    int Size,
    bool WasAiSearch,
    string? ParsedQueryDebug);

public sealed record SearchResultItemResponse(
    string ProductId,
    string Name,
    string Category,
    decimal Price,
    string Currency,
    double RelevanceScore,
    IReadOnlyList<string> ImageUrls);

public sealed record StreamSearchEvent(
    string Phase,
    IReadOnlyList<SearchResultItemResponse> Items,
    int TotalCount,
    bool WasAiSearch);

public sealed record SimilarItemResponse(
    string CatalogItemId,
    double Score);

public sealed record SimilarItemsResponse(
    IReadOnlyList<SimilarItemResponse> Items);

public sealed record CoOccurrenceItemResponse(
    string CatalogItemId,
    double Score);

public sealed record FrequentlyBoughtTogetherResponse(
    IReadOnlyList<CoOccurrenceItemResponse> Items);
