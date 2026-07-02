namespace Application.Models;

public enum FailedCompensationRetryStatus
{
    Pending = 0,
    Completed = 1,
    Exhausted = 2,
    InProgress = 3,
}

public sealed class FailedCompensationRetry
{
    public Guid Id { get; private set; }
    public Guid SagaId { get; private set; }
    public string SagaType { get; private set; } = string.Empty;
    public string LastFailedStep { get; private set; } = string.Empty;
    public int RetryCount { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public FailedCompensationRetryStatus Status { get; private set; }
    public string? LastError { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private FailedCompensationRetry() { }

    private FailedCompensationRetry(
        Guid id,
        Guid sagaId,
        string sagaType,
        string lastFailedStep,
        string? lastError,
        DateTime createdAtUtc)
    {
        Id = id;
        SagaId = sagaId;
        SagaType = sagaType;
        LastFailedStep = lastFailedStep;
        LastError = lastError;
        RetryCount = 0;
        NextAttemptAtUtc = createdAtUtc;
        Status = FailedCompensationRetryStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public static FailedCompensationRetry Create(
        Guid sagaId,
        string sagaType,
        string lastFailedStep,
        string? lastError,
        DateTime? createdAtUtc = null)
    {
        if (sagaId == Guid.Empty)
            throw new ArgumentException("SagaId cannot be empty.", nameof(sagaId));
        if (string.IsNullOrWhiteSpace(sagaType))
            throw new ArgumentException("SagaType cannot be empty.", nameof(sagaType));
        if (string.IsNullOrWhiteSpace(lastFailedStep))
            throw new ArgumentException("LastFailedStep cannot be empty.", nameof(lastFailedStep));

        var now = createdAtUtc ?? DateTime.UtcNow;
        return new FailedCompensationRetry(
            Guid.NewGuid(),
            sagaId,
            sagaType.Trim(),
            lastFailedStep.Trim(),
            lastError?.Trim(),
            now);
    }

    public void MarkInProgress(DateTime claimedAtUtc)
    {
        if (Status != FailedCompensationRetryStatus.Pending) return;
        Status = FailedCompensationRetryStatus.InProgress;
        UpdatedAtUtc = claimedAtUtc;
    }

    // Return a claimed row to Pending WITHOUT consuming a retry attempt. Used when the row was
    // claimed but could not be processed (e.g. the saga's distributed lock is held by another
    // worker), so it must be retried later without counting against the retry budget
    public void Reschedule(DateTime nextAttemptAtUtc, DateTime rescheduledAtUtc)
    {
        Status = FailedCompensationRetryStatus.Pending;
        NextAttemptAtUtc = nextAttemptAtUtc;
        UpdatedAtUtc = rescheduledAtUtc;
    }

    public void MarkAttemptFailed(string error, DateTime nextAttemptAtUtc, DateTime attemptedAtUtc)
    {
        RetryCount++;
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown compensation error" : error.Trim();
        NextAttemptAtUtc = nextAttemptAtUtc;
        Status = FailedCompensationRetryStatus.Pending;
        UpdatedAtUtc = attemptedAtUtc;
    }

    public void MarkCompleted(DateTime completedAtUtc)
    {
        Status = FailedCompensationRetryStatus.Completed;
        CompletedAtUtc = completedAtUtc;
        NextAttemptAtUtc = DateTime.MaxValue;
        UpdatedAtUtc = completedAtUtc;
    }

    public void MarkExhausted(string error, DateTime exhaustedAtUtc)
    {
        RetryCount++;
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown compensation error" : error.Trim();
        Status = FailedCompensationRetryStatus.Exhausted;
        CompletedAtUtc = exhaustedAtUtc;
        NextAttemptAtUtc = DateTime.MaxValue;
        UpdatedAtUtc = exhaustedAtUtc;
    }
}
