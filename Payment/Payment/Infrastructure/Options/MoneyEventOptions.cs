namespace Infrastructure.Options;

public sealed class MoneyEventOptions
{
    public const string SectionName = "MoneyEvent";

    public int PollIntervalSeconds { get; init; } = 5;

    public int BatchSize { get; init; } = 100;

    public int MaxAttempts { get; init; } = 8;

    public int BaseRetryDelaySeconds { get; init; } = 5;

    public int MaxRetryDelaySeconds { get; init; } = 300;
}
