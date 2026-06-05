namespace Application.Models;

public enum CompensationRefundRetryStatus
{
    Pending = 0,
    Completed = 1,
    Exhausted = 2,
    InProgress = 3,
}

public sealed class CompensationRefundRetry
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string PaymentId { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public int RetryCount { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public CompensationRefundRetryStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private CompensationRefundRetry()
    {
    }

    private CompensationRefundRetry(
        Guid id,
        Guid orderId,
        string paymentId,
        decimal amount,
        string currency,
        string reason,
        DateTime createdAtUtc)
    {
        Id = id;
        OrderId = orderId;
        PaymentId = paymentId;
        Amount = amount;
        Currency = currency;
        Reason = reason;
        RetryCount = 0;
        NextAttemptAtUtc = createdAtUtc;
        Status = CompensationRefundRetryStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public static CompensationRefundRetry Create(
        Guid orderId,
        string paymentId,
        decimal amount,
        string currency,
        string reason,
        DateTime? createdAtUtc = null)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("OrderId cannot be empty", nameof(orderId));
        }

        if (string.IsNullOrWhiteSpace(paymentId))
        {
            throw new ArgumentException("PaymentId cannot be empty", nameof(paymentId));
        }

        if (amount <= 0)
        {
            throw new ArgumentException("Amount must be greater than zero", nameof(amount));
        }

        var now = createdAtUtc ?? DateTime.UtcNow;

        return new CompensationRefundRetry(
            Guid.NewGuid(),
            orderId,
            paymentId.Trim(),
            amount,
            string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(reason) ? "Order cancelled - saga compensation" : reason.Trim(),
            now);
    }

    public bool IsPending => Status == CompensationRefundRetryStatus.Pending;

    public void MarkInProgress(DateTime? claimedAtUtc = null)
    {
        if (Status != CompensationRefundRetryStatus.Pending) return;
        Status = CompensationRefundRetryStatus.InProgress;
        UpdatedAtUtc = claimedAtUtc ?? DateTime.UtcNow;
    }

    public void MarkAttemptFailed(string error, DateTime nextAttemptAtUtc, DateTime? attemptedAtUtc = null)
    {
        if (Status != CompensationRefundRetryStatus.Pending
            && Status != CompensationRefundRetryStatus.InProgress)
        {
            return;
        }

        var now = attemptedAtUtc ?? DateTime.UtcNow;
        RetryCount++;
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown compensation refund error" : error.Trim();
        NextAttemptAtUtc = nextAttemptAtUtc;
        Status = CompensationRefundRetryStatus.Pending;
        UpdatedAtUtc = now;
    }

    public void MarkPendingVerification(DateTime nextAttemptAtUtc, DateTime? updatedAtUtc = null)
    {
        if (Status != CompensationRefundRetryStatus.Pending
            && Status != CompensationRefundRetryStatus.InProgress)
        {
            return;
        }

        var now = updatedAtUtc ?? DateTime.UtcNow;
        NextAttemptAtUtc = nextAttemptAtUtc;
        Status = CompensationRefundRetryStatus.Pending;
        UpdatedAtUtc = now;
    }

    public void MarkCompleted(DateTime? completedAtUtc = null)
    {
        if (Status != CompensationRefundRetryStatus.Pending
            && Status != CompensationRefundRetryStatus.InProgress)
        {
            return;
        }

        var now = completedAtUtc ?? DateTime.UtcNow;
        Status = CompensationRefundRetryStatus.Completed;
        CompletedAtUtc = now;
        NextAttemptAtUtc = DateTime.MaxValue;
        UpdatedAtUtc = now;
    }

    public void MarkExhausted(string error, DateTime? exhaustedAtUtc = null)
    {
        if (Status != CompensationRefundRetryStatus.Pending
            && Status != CompensationRefundRetryStatus.InProgress)
        {
            return;
        }

        var now = exhaustedAtUtc ?? DateTime.UtcNow;
        RetryCount++;
        Status = CompensationRefundRetryStatus.Exhausted;
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown compensation refund error" : error.Trim();
        CompletedAtUtc = now;
        NextAttemptAtUtc = DateTime.MaxValue;
        UpdatedAtUtc = now;
    }
}
