using Application.Models;

namespace Application.Tests.Models;

public class CompensationRefundRetryTests
{
    [Fact]
    public void Create_ShouldNormalizeFields_AndApplyDefaults()
    {
        var orderId = Guid.NewGuid();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var retry = CompensationRefundRetry.Create(
            orderId,
            "  PAY-123  ",
            49.99m,
            " usd ",
            "   ",
            now);

        Assert.Equal(orderId, retry.OrderId);
        Assert.Equal("PAY-123", retry.PaymentId);
        Assert.Equal("USD", retry.Currency);
        Assert.Equal("Order cancelled - saga compensation", retry.Reason);
        Assert.Equal(CompensationRefundRetryStatus.Pending, retry.Status);
        Assert.Equal(now, retry.NextAttemptAtUtc);
        Assert.Equal(now, retry.CreatedAtUtc);
        Assert.Equal(now, retry.UpdatedAtUtc);
        Assert.True(retry.IsPending);
    }

    [Fact]
    public void Create_ShouldThrow_WhenOrderIdIsEmpty()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CompensationRefundRetry.Create(Guid.Empty, "PAY-1", 10m, "USD", "reason"));

        Assert.Equal("orderId", ex.ParamName);
    }

    [Fact]
    public void Create_ShouldThrow_WhenAmountIsNonPositive()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CompensationRefundRetry.Create(Guid.NewGuid(), "PAY-1", 0m, "USD", "reason"));

        Assert.Equal("amount", ex.ParamName);
    }

    [Fact]
    public void MarkAttemptFailed_ShouldIncrementRetryAndKeepPending_WhenPending()
    {
        var initial = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var attemptedAt = initial.AddMinutes(1);
        var nextAttempt = initial.AddMinutes(3);

        var retry = CompensationRefundRetry.Create(
            Guid.NewGuid(),
            "PAY-123",
            25m,
            "USD",
            "test",
            initial);

        retry.MarkAttemptFailed(" temporary outage ", nextAttempt, attemptedAt);

        Assert.Equal(1, retry.RetryCount);
        Assert.Equal("temporary outage", retry.LastError);
        Assert.Equal(nextAttempt, retry.NextAttemptAtUtc);
        Assert.Equal(attemptedAt, retry.UpdatedAtUtc);
        Assert.Equal(CompensationRefundRetryStatus.Pending, retry.Status);
        Assert.Null(retry.CompletedAtUtc);
    }

    [Fact]
    public void MarkCompleted_ShouldTransitionToCompleted_AndBecomeTerminal()
    {
        var initial = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var completedAt = initial.AddMinutes(2);

        var retry = CompensationRefundRetry.Create(
            Guid.NewGuid(),
            "PAY-123",
            25m,
            "USD",
            "test",
            initial);

        retry.MarkCompleted(completedAt);
        retry.MarkAttemptFailed("ignored", completedAt.AddMinutes(2), completedAt.AddMinutes(1));

        Assert.Equal(CompensationRefundRetryStatus.Completed, retry.Status);
        Assert.Equal(completedAt, retry.CompletedAtUtc);
        Assert.Equal(DateTime.MaxValue, retry.NextAttemptAtUtc);
        Assert.Equal(0, retry.RetryCount);
        Assert.False(retry.IsPending);
    }

    [Fact]
    public void MarkPendingVerification_ShouldRescheduleWithoutIncrementingRetryCount()
    {
        var initial = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var rescheduledAt = initial.AddMinutes(5);
        var updatedAt = initial.AddMinutes(1);

        var retry = CompensationRefundRetry.Create(
            Guid.NewGuid(),
            "PAY-123",
            25m,
            "USD",
            "test",
            initial);

        retry.MarkInProgress(updatedAt);
        retry.MarkPendingVerification(rescheduledAt, updatedAt);

        Assert.Equal(CompensationRefundRetryStatus.Pending, retry.Status);
        Assert.Equal(0, retry.RetryCount);
        Assert.Equal(rescheduledAt, retry.NextAttemptAtUtc);
        Assert.Equal(updatedAt, retry.UpdatedAtUtc);
        Assert.Null(retry.CompletedAtUtc);
    }

    [Fact]
    public void MarkExhausted_ShouldIncrementRetry_AndBecomeTerminal()
    {
        var initial = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var exhaustedAt = initial.AddMinutes(4);

        var retry = CompensationRefundRetry.Create(
            Guid.NewGuid(),
            "PAY-123",
            25m,
            "USD",
            "test",
            initial);

        retry.MarkAttemptFailed("first", initial.AddMinutes(1), initial.AddSeconds(10));
        retry.MarkExhausted(" permanent failure ", exhaustedAt);
        retry.MarkCompleted(exhaustedAt.AddMinutes(1));

        Assert.Equal(CompensationRefundRetryStatus.Exhausted, retry.Status);
        Assert.Equal(2, retry.RetryCount);
        Assert.Equal("permanent failure", retry.LastError);
        Assert.Equal(exhaustedAt, retry.CompletedAtUtc);
        Assert.Equal(DateTime.MaxValue, retry.NextAttemptAtUtc);
        Assert.False(retry.IsPending);
    }
}
