namespace Application.Models;

public enum PplPendingBookingStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
    Exhausted = 3,
    InProgress = 4,
}

public sealed class PplPendingBooking
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string ReferenceId { get; private set; } = string.Empty;
    public int AttemptCount { get; private set; }
    public DateTime NextRetryAtUtc { get; private set; }
    public PplPendingBookingStatus Status { get; private set; }
    public string? LastError { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private PplPendingBooking() { }

    private PplPendingBooking(Guid id, Guid orderId, string referenceId, DateTime createdAtUtc)
    {
        Id = id;
        OrderId = orderId;
        ReferenceId = referenceId;
        AttemptCount = 0;
        Status = PplPendingBookingStatus.Pending;
        NextRetryAtUtc = createdAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public static PplPendingBooking Create(Guid orderId, string referenceId, DateTime? createdAtUtc = null)
    {
        if (orderId == Guid.Empty)
            throw new ArgumentException("OrderId cannot be empty.", nameof(orderId));
        if (string.IsNullOrWhiteSpace(referenceId))
            throw new ArgumentException("ReferenceId cannot be empty.", nameof(referenceId));

        var now = createdAtUtc ?? DateTime.UtcNow;
        return new PplPendingBooking(Guid.NewGuid(), orderId, referenceId.Trim(), now);
    }

    public void MarkInProgress(DateTime claimedAtUtc)
    {
        if (Status != PplPendingBookingStatus.Pending) return;
        Status = PplPendingBookingStatus.InProgress;
        UpdatedAtUtc = claimedAtUtc;
    }

    public void MarkAccepted(DateTime resolvedAtUtc)
    {
        Status = PplPendingBookingStatus.Accepted;
        UpdatedAtUtc = resolvedAtUtc;
    }

    public void MarkAttemptFailed(string error, DateTime nextRetryAtUtc, DateTime attemptedAtUtc)
    {
        AttemptCount++;
        LastError = error;
        NextRetryAtUtc = nextRetryAtUtc;
        Status = PplPendingBookingStatus.Pending;
        UpdatedAtUtc = attemptedAtUtc;
    }

    public void MarkExhausted(string error, DateTime exhaustedAtUtc)
    {
        AttemptCount++;
        LastError = error;
        Status = PplPendingBookingStatus.Exhausted;
        UpdatedAtUtc = exhaustedAtUtc;
    }
}
