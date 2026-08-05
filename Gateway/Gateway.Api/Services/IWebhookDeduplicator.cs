namespace Gateway.Api.Services;

public interface IWebhookDeduplicator
{
    Task<bool> IsDuplicateAsync(string key, CancellationToken ct);

    Task MarkProcessedAsync(string key, TimeSpan ttl, CancellationToken ct);
}
