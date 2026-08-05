using StackExchange.Redis;

namespace Gateway.Api.Services;

// Fail-open: a Redis outage must never drop a terminal carrier webhook — the saga-resume
// layer (lock + status re-check) is the correctness backstop, this is an edge optimisation
public sealed class RedisWebhookDeduplicator(
    IConnectionMultiplexer redis,
    ILogger<RedisWebhookDeduplicator> logger) : IWebhookDeduplicator
{
    public async Task<bool> IsDuplicateAsync(string key, CancellationToken ct)
    {
        try
        {
            return await redis.GetDatabase().KeyExistsAsync(key);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis dedup check failed for {Key}; proceeding as not-duplicate.", key);
            return false;
        }
    }

    public async Task MarkProcessedAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(key, "1", ttl);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis dedup marker write failed for {Key}.", key);
        }
    }
}
