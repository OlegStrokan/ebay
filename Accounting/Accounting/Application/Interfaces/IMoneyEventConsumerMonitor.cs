namespace Application.Interfaces;

public sealed record MoneyEventConsumerSnapshot(
    long Lag,
    long TotalSkipped,
    IReadOnlyList<string> SkippedSinceLastSnapshot,
    DateTime? LastProcessedAtUtc);

// The consumer commits past a message it cannot post, which knowingly leaves the ledger short.
// Nothing else watches for that, so the count is surfaced here for the reconciliation worker.
public interface IMoneyEventConsumerMonitor
{
    void RecordProcessed(long lag, DateTime processedAtUtc);

    void RecordSkipped(string eventId);

    // Clears the skipped list so one skip is alerted once, not on every cycle for ever.
    MoneyEventConsumerSnapshot TakeSnapshot();
}
