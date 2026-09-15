namespace Infrastructure.Options;

public sealed class ReconciliationOptions
{
    public const string SectionName = "LedgerReconciliation";

    public bool Enabled { get; init; } = true;

    public int IntervalMinutes { get; init; } = 15;

    public int StartupDelaySeconds { get; init; } = 60;

    public int MaxFindings { get; init; } = 20;

    // 0 disables the lag check.
    public long MaxAcceptableLag { get; init; } = 1000;
}
