namespace Infrastructure.Options;

public sealed class MoneyEventConsumerOptions
{
    public const string SectionName = "MoneyEventConsumer";

    // for tests, or a local run against Postgres only
    public bool Enabled { get; init; } = true;

    public string ConsumerGroupId { get; init; } = "accounting-service-money-events";

    public int MaxOffsetRetries { get; init; } = 5;

    public int RetryDelaySeconds { get; init; } = 5;
}
