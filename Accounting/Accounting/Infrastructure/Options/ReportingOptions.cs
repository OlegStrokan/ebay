namespace Infrastructure.Options;

public sealed class ReportingOptions
{
    public const string SectionName = "Reporting";

    public bool Enabled { get; init; } = true;

    public string ReportingCurrency { get; init; } = "USD";

    public int IntervalMinutes { get; init; } = 5;

    public int StartupDelaySeconds { get; init; } = 30;

    public int BatchSize { get; init; } = 200;

    // Placeholder for a real rate feed: seeded once at startup if the row is absent.
    public List<SeedFxRate> SeedRates { get; init; } = [];
}

public sealed class SeedFxRate
{
    public string BaseCurrency { get; init; } = string.Empty;

    public string QuoteCurrency { get; init; } = string.Empty;

    public decimal Rate { get; init; }

    public DateTime EffectiveFrom { get; init; } = DateTime.UnixEpoch;
}
