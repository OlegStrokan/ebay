using Application.RetryStore;
using Catalog.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Catalog.IntegrationTests.RetryStore;

[TestFixture]
public sealed class PostgresRetryStoreTests : PostgresFixture
{
    #region PersistAsync

    [Test]
    public async Task PersistAsync_ShouldCreateRecord_RetrievableByGetDueRecords()
    {
        var store = CreateStore();
        var record = BuildRecord(nextRetryAt: DateTime.UtcNow.AddMinutes(-1));

        await store.PersistAsync(record);

        var due = await store.GetDueRecordsAsync(100);

        due.Should().Contain(r => r.Id == record.Id);
    }

    [Test]
    public async Task PersistAsync_ShouldStoreAllFields()
    {
        var store = CreateStore();
        var record = BuildRecord();

        await store.PersistAsync(record);

        var due = await store.GetDueRecordsAsync(100);
        var persisted = due.First(r => r.Id == record.Id);

        persisted.EventId.Should().Be(record.EventId);
        persisted.EventType.Should().Be(record.EventType);
        persisted.Topic.Should().Be(record.Topic);
        persisted.Partition.Should().Be(record.Partition);
        persisted.Offset.Should().Be(record.Offset);
        persisted.MessageKey.Should().Be(record.MessageKey);
        persisted.Payload.Should().Be(record.Payload);
        persisted.Headers.Should().Be(record.Headers);
        persisted.RetryCount.Should().Be(record.RetryCount);
        persisted.Status.Should().Be(RetryRecordStatus.InProgress);
        persisted.LastErrorMessage.Should().Be(record.LastErrorMessage);
        persisted.LastErrorType.Should().Be(record.LastErrorType);
        persisted.CorrelationId.Should().Be(record.CorrelationId);
    }

    [Test]
    public async Task PersistAsync_WithNullOptionalFields_ShouldSucceed()
    {
        var store = CreateStore();
        var record = BuildRecord();
        record.EventId = null;
        record.MessageKey = null;
        record.Headers = null;
        record.LastErrorMessage = null;
        record.LastErrorType = null;
        record.CorrelationId = null;

        await store.PersistAsync(record);

        var due = await store.GetDueRecordsAsync(100);
        var persisted = due.First(r => r.Id == record.Id);

        persisted.EventId.Should().BeNull();
        persisted.MessageKey.Should().BeNull();
        persisted.Headers.Should().BeNull();
        persisted.LastErrorMessage.Should().BeNull();
        persisted.LastErrorType.Should().BeNull();
        persisted.CorrelationId.Should().BeNull();
    }

    #endregion

    #region GetDueRecordsAsync

    [Test]
    public async Task GetDueRecordsAsync_ShouldOnlyReturnPendingRecords()
    {
        var store = CreateStore();

        var pending = BuildRecord(RetryRecordStatus.Pending);
        var inProgress = BuildRecord(RetryRecordStatus.InProgress);
        var succeeded = BuildRecord(RetryRecordStatus.Succeeded);
        var deadLetter = BuildRecord(RetryRecordStatus.DeadLetter);

        await store.PersistAsync(pending);
        await store.PersistAsync(inProgress);
        await store.PersistAsync(succeeded);
        await store.PersistAsync(deadLetter);

        var due = await store.GetDueRecordsAsync(100);

        due.Should().Contain(r => r.Id == pending.Id);
        due.Should().NotContain(r => r.Id == inProgress.Id);
        due.Should().NotContain(r => r.Id == succeeded.Id);
        due.Should().NotContain(r => r.Id == deadLetter.Id);
    }

    [Test]
    public async Task GetDueRecordsAsync_ShouldNotReturnFutureRecords()
    {
        var store = CreateStore();

        var past = BuildRecord(nextRetryAt: DateTime.UtcNow.AddMinutes(-5));
        var future = BuildRecord(nextRetryAt: DateTime.UtcNow.AddMinutes(60));

        await store.PersistAsync(past);
        await store.PersistAsync(future);

        var due = await store.GetDueRecordsAsync(100);

        due.Should().Contain(r => r.Id == past.Id);
        due.Should().NotContain(r => r.Id == future.Id);
    }

    [Test]
    public async Task GetDueRecordsAsync_ShouldRespectBatchSizeLimit()
    {
        var store = CreateStore();

        for (var i = 0; i < 5; i++)
            await store.PersistAsync(BuildRecord());

        var due = await store.GetDueRecordsAsync(2);

        due.Count.Should().BeLessOrEqualTo(2);
    }

    [Test]
    public async Task GetDueRecordsAsync_ShouldOrderByNextRetryAt()
    {
        var store = CreateStore();

        var older = BuildRecord(nextRetryAt: DateTime.UtcNow.AddMinutes(-10));
        var newer = BuildRecord(nextRetryAt: DateTime.UtcNow.AddMinutes(-1));

        await store.PersistAsync(newer);
        await store.PersistAsync(older);

        var due = await store.GetDueRecordsAsync(100);

        var oldIdx = due.ToList().FindIndex(r => r.Id == older.Id);
        var newIdx = due.ToList().FindIndex(r => r.Id == newer.Id);

        if (oldIdx >= 0 && newIdx >= 0)
            oldIdx.Should().BeLessThan(newIdx, "older records should be returned first");
    }

    #endregion

    #region MarkInProgressAsync

    [Test]
    public async Task MarkInProgressAsync_ShouldChangeStatusToInProgress()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);

        await store.MarkInProgressAsync(record.Id);

        var updated = await ReadRecordDirectAsync(record.Id);
        updated!.Status.Should().Be(RetryRecordStatus.InProgress);
    }

    [Test]
    public async Task MarkInProgressAsync_ShouldRemoveRecordFromDueResults()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);

        await store.MarkInProgressAsync(record.Id);

        var due = await store.GetDueRecordsAsync(100);
        due.Should().NotContain(r => r.Id == record.Id);
    }

    #endregion

    #region MarkSucceededAsync

    [Test]
    public async Task MarkSucceededAsync_ShouldChangeStatusToSucceeded()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        await store.MarkSucceededAsync(record.Id);

        var updated = await ReadRecordDirectAsync(record.Id);
        updated!.Status.Should().Be(RetryRecordStatus.Succeeded);
    }

    #endregion

    #region RescheduleAsync

    [Test]
    public async Task RescheduleAsync_ShouldSetStatusBackToPending()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        var newNextRetry = DateTime.UtcNow.AddMinutes(5);
        await store.RescheduleAsync(record.Id, 1, newNextRetry, "retry error", "System.Exception");

        var updated = await ReadRecordDirectAsync(record.Id);
        updated!.Status.Should().Be(RetryRecordStatus.Pending);
    }

    [Test]
    public async Task RescheduleAsync_ShouldUpdateRetryCount()
    {
        var store = CreateStore();
        var record = BuildRecord(retryCount: 2);
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        await store.RescheduleAsync(record.Id, 3, DateTime.UtcNow.AddMinutes(5), "err", "Type");

        var updated = await ReadRecordDirectAsync(record.Id);
        updated!.RetryCount.Should().Be(3);
    }

    [Test]
    public async Task RescheduleAsync_ShouldUpdateErrorInfo()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        await store.RescheduleAsync(record.Id, 1, DateTime.UtcNow.AddMinutes(5),
            "new error message", "System.InvalidOperationException");

        var updated = await ReadRecordDirectAsync(record.Id);
        updated!.LastErrorMessage.Should().Be("new error message");
        updated.LastErrorType.Should().Be("System.InvalidOperationException");
    }

    [Test]
    public async Task RescheduleAsync_ShouldUpdateNextRetryAt()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        var futureRetry = DateTime.UtcNow.AddMinutes(10);
        await store.RescheduleAsync(record.Id, 1, futureRetry, null, null);

        var updated = await ReadRecordDirectAsync(record.Id);
        updated!.NextRetryAt.Should().BeCloseTo(futureRetry, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RescheduleAsync_FutureNextRetryAt_ShouldNotBeReturnedByGetDueRecords()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        await store.RescheduleAsync(record.Id, 1, DateTime.UtcNow.AddMinutes(30), null, null);

        var due = await store.GetDueRecordsAsync(100);
        due.Should().NotContain(r => r.Id == record.Id);
    }

    #endregion

    #region MarkDeadLetterAsync

    [Test]
    public async Task MarkDeadLetterAsync_ShouldChangeStatusToDeadLetter()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        await store.MarkDeadLetterAsync(record.Id, "final error", "System.Exception");

        var updated = await ReadRecordDirectAsync(record.Id);
        updated!.Status.Should().Be(RetryRecordStatus.DeadLetter);
    }

    [Test]
    public async Task MarkDeadLetterAsync_ShouldUpdateErrorInfo()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        await store.MarkDeadLetterAsync(record.Id, "dead letter error", "System.InvalidOperationException");

        var updated = await ReadRecordDirectAsync(record.Id);
        updated!.LastErrorMessage.Should().Be("dead letter error");
        updated.LastErrorType.Should().Be("System.InvalidOperationException");
    }

    [Test]
    public async Task MarkDeadLetterAsync_ShouldNotBeReturnedByGetDueRecords()
    {
        var store = CreateStore();
        var record = BuildRecord();
        await store.PersistAsync(record);
        await store.MarkInProgressAsync(record.Id);

        await store.MarkDeadLetterAsync(record.Id, "done", "err");

        var due = await store.GetDueRecordsAsync(100);
        due.Should().NotContain(r => r.Id == record.Id);
    }

    #endregion

    #region Full lifecycles

    [Test]
    public async Task FullLifecycle_Persist_InProgress_Succeeded()
    {
        var store = CreateStore();
        var record = BuildRecord();

        await store.PersistAsync(record);
        var step1 = await ReadRecordDirectAsync(record.Id);
        step1!.Status.Should().Be(RetryRecordStatus.Pending);

        await store.MarkInProgressAsync(record.Id);
        var step2 = await ReadRecordDirectAsync(record.Id);
        step2!.Status.Should().Be(RetryRecordStatus.InProgress);

        await store.MarkSucceededAsync(record.Id);
        var step3 = await ReadRecordDirectAsync(record.Id);
        step3!.Status.Should().Be(RetryRecordStatus.Succeeded);
    }

    [Test]
    public async Task FullLifecycle_Persist_InProgress_Reschedule_InProgress_DeadLetter()
    {
        var store = CreateStore();
        var record = BuildRecord();

        await store.PersistAsync(record);

        await store.MarkInProgressAsync(record.Id);
        await store.RescheduleAsync(record.Id, 1, DateTime.UtcNow.AddMinutes(-1), "err1", "Type1");

        var step2 = await ReadRecordDirectAsync(record.Id);
        step2!.Status.Should().Be(RetryRecordStatus.Pending);
        step2.RetryCount.Should().Be(1);

        await store.MarkInProgressAsync(record.Id);
        await store.MarkDeadLetterAsync(record.Id, "final", "Type2");

        var step3 = await ReadRecordDirectAsync(record.Id);
        step3!.Status.Should().Be(RetryRecordStatus.DeadLetter);
        step3.LastErrorMessage.Should().Be("final");
    }

    #endregion
}
