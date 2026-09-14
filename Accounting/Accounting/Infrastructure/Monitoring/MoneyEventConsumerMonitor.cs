using Application.Interfaces;

namespace Infrastructure.Monitoring;

internal sealed class MoneyEventConsumerMonitor : IMoneyEventConsumerMonitor
{
    private readonly object _gate = new();
    private readonly List<string> _skippedSinceLastSnapshot = [];

    private long _lag;
    private long _totalSkipped;
    private DateTime? _lastProcessedAtUtc;

    public void RecordProcessed(long lag, DateTime processedAtUtc)
    {
        lock (_gate)
        {
            _lag = lag < 0 ? 0 : lag;
            _lastProcessedAtUtc = processedAtUtc;
        }
    }

    public void RecordSkipped(string eventId)
    {
        lock (_gate)
        {
            _totalSkipped++;
            _skippedSinceLastSnapshot.Add(string.IsNullOrWhiteSpace(eventId) ? "unknown" : eventId);
        }
    }

    public MoneyEventConsumerSnapshot TakeSnapshot()
    {
        lock (_gate)
        {
            var skipped = _skippedSinceLastSnapshot.ToArray();
            _skippedSinceLastSnapshot.Clear();

            return new MoneyEventConsumerSnapshot(_lag, _totalSkipped, skipped, _lastProcessedAtUtc);
        }
    }
}
