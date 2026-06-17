using Application.Gateways;
using Application.Queries.SearchProducts;

namespace Infrastructure.AiSearch;

// Registered when AiSearch:Enabled = false
public sealed class NullAiSearchStreamGateway : IAiSearchStreamGateway
{
    public IAsyncEnumerable<StreamSearchResult> SearchStreamAsync(
        string query, int page, int size, string? userId, CancellationToken ct)
        => throw new NotSupportedException(
            "AI stream search is disabled. Set AiSearch:Enabled = true.");
}
