using System.Text.Json;
using Application.Consumers;
using Application.RetryStore;
using Infrastructure.Kafka;
using Infrastructure.RetryStore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Infrastructure.Tests.Kafka;

[TestFixture]
public class RetryWorkerBackgroundServiceTests
{
    private IRetryStore _retryStore;
    private IProductEventConsumer _consumer;
    private RetryWorkerBackgroundService _sut;
    private RetryStoreOptions _options;
    private int _pollCount;

    [SetUp]
    public void SetUp()
    {
        _pollCount = 0;
        _retryStore = Substitute.For<IRetryStore>();
        _consumer = Substitute.For<IProductEventConsumer>();
        _consumer.EventType.Returns("ProductCreatedEvent");

        _options = new RetryStoreOptions
        {
            WorkerRetryLimit = 3,
            WorkerPollIntervalSeconds = 1,
            WorkerBatchSize = 10,
        };

        var serviceProvider = BuildServiceProvider(_retryStore, _consumer);
        var logger = Substitute.For<ILogger<RetryWorkerBackgroundService>>();

        _sut = new RetryWorkerBackgroundService(
            serviceProvider,
            Options.Create(_options),
            logger);
    }

    [TearDown]
    public void TearDown() => _sut.Dispose();

    [Test]
    public async Task ProcessDueRecords_WhenNoDueRecords_ShouldNotTouchTheStore()
    {
        SetupGetDueRecords([]);

        await RunWorkerOnce();

        await _retryStore.DidNotReceiveWithAnyArgs()
            .MarkSucceededAsync(default, default);
        await _retryStore.DidNotReceiveWithAnyArgs()
            .RescheduleAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task ProcessDueRecords_WhenProjectionSucceeds_ShouldMarkSucceeded()
    {
        var record = BuildRecord("ProductCreatedEvent");
        SetupGetDueRecords([record]);

        await RunWorkerOnce();

        // GetDueRecordsAsync claims rows as InProgress in the same statement, so the worker
        // never calls MarkInProgressAsync.
        await _retryStore.DidNotReceiveWithAnyArgs().MarkInProgressAsync(default, default);
        await _retryStore.Received(1).MarkSucceededAsync(record.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDueRecords_WhenProjectionSucceeds_ShouldDispatchToConsumer()
    {
        var record = BuildRecord("ProductCreatedEvent");
        SetupGetDueRecords([record]);

        await RunWorkerOnce();

        await _consumer.Received(1).ConsumeAsync(Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDueRecords_WhenProjectionFails_UnderLimit_ShouldReschedule()
    {
        var record = BuildRecord("ProductCreatedEvent", retryCount: 1);
        SetupGetDueRecords([record]);

        _consumer.ConsumeAsync(Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mapping error"));

        await RunWorkerOnce();

        await _retryStore.Received(1).RescheduleAsync(
            record.Id,
            2, // retryCount + 1
            Arg.Any<DateTime>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDueRecords_WhenProjectionFails_AtLimit_ShouldMoveToDeadLetter()
    {
        // WorkerRetryLimit = 3, so retryCount 2 → newRetryCount 3 ≥ limit → dead letter
        var record = BuildRecord("ProductCreatedEvent", retryCount: 2);
        SetupGetDueRecords([record]);

        _consumer.ConsumeAsync(Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("permanent failure"));

        await RunWorkerOnce();

        await _retryStore.Received(1).MarkDeadLetterAsync(
            record.Id,
            Arg.Is<string?>(s => s != null && s.Contains("permanent failure")),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDueRecords_WhenProjectionFails_AboveLimit_ShouldMoveToDeadLetter()
    {
        var record = BuildRecord("ProductCreatedEvent", retryCount: 10);
        SetupGetDueRecords([record]);

        _consumer.ConsumeAsync(Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("still failing"));

        await RunWorkerOnce();

        await _retryStore.Received(1).MarkDeadLetterAsync(
            record.Id,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await _retryStore.DidNotReceiveWithAnyArgs()
            .RescheduleAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task ProcessDueRecords_WhenPayloadInvalid_ShouldReschedule()
    {
        var record = BuildRecord("ProductCreatedEvent");
        record.Payload = "not-valid-json{{{";
        SetupGetDueRecords([record]);

        await RunWorkerOnce();

        // Invalid JSON causes JsonException which is caught and treated as a failure.
        // With retryCount=0, newRetryCount=1 < limit=3, so it reschedules (not dead-letter).
        await _retryStore.Received(1).RescheduleAsync(
            record.Id,
            Arg.Any<int>(),
            Arg.Any<DateTime>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _retryStore.DidNotReceiveWithAnyArgs().MarkSucceededAsync(default, default);
    }

    [Test]
    public async Task ProcessDueRecords_WhenPayloadIsNullJson_ShouldMoveToDeadLetter()
    {
        var record = BuildRecord("ProductCreatedEvent");
        record.Payload = "null";
        SetupGetDueRecords([record]);

        await RunWorkerOnce();

        await _retryStore.Received(1).MarkDeadLetterAsync(
            record.Id,
            Arg.Is<string?>(s => s != null && s.Contains("deserialize")),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDueRecords_WhenNoConsumerRegistered_ShouldMarkSucceeded()
    {
        // Event type with no matching consumer → dispatch is a no-op → success
        var record = BuildRecord("UnknownEventType");
        SetupGetDueRecords([record]);

        await RunWorkerOnce();

        await _retryStore.Received(1).MarkSucceededAsync(record.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDueRecords_ShouldProcessMultipleRecords()
    {
        var record1 = BuildRecord("ProductCreatedEvent");
        var record2 = BuildRecord("ProductCreatedEvent");
        SetupGetDueRecords([record1, record2]);

        await RunWorkerOnce();

        await _retryStore.Received(1).MarkSucceededAsync(record1.Id, Arg.Any<CancellationToken>());
        await _retryStore.Received(1).MarkSucceededAsync(record2.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessDueRecords_RescheduleError_ShouldIncludeErrorType()
    {
        var record = BuildRecord("ProductCreatedEvent", retryCount: 0);
        SetupGetDueRecords([record]);

        _consumer.ConsumeAsync(Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("some error"));

        await RunWorkerOnce();

        await _retryStore.Received(1).RescheduleAsync(
            record.Id,
            1,
            Arg.Any<DateTime>(),
            Arg.Is<string?>(s => s!.Contains("some error")),
            Arg.Is<string?>(s => s!.Contains("InvalidOperationException")),
            Arg.Any<CancellationToken>());
    }

    // ------- Helpers -------
    
    /// Returns the given records on the first poll, then empty on all subsequent polls.
    /// This ensures the worker processes each record exactly once.
    private void SetupGetDueRecords(IReadOnlyList<RetryRecord> firstPollRecords)
    {
        _retryStore.GetDueRecordsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var call = Interlocked.Increment(ref _pollCount);
                return call == 1
                    ? firstPollRecords
                    : (IReadOnlyList<RetryRecord>)[];
            });
    }

    private async Task RunWorkerOnce()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await _sut.StartAsync(cts.Token);
            // Give time for one cycle (poll interval is 1s)
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            await _sut.StopAsync(CancellationToken.None);
        }
    }

    private static RetryRecord BuildRecord(string eventType, int retryCount = 0)
    {
        var wrapper = new EventWrapper
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Payload = JsonDocument.Parse("{}").RootElement,
            OccurredOn = DateTime.UtcNow,
        };

        return new RetryRecord
        {
            Id = Guid.NewGuid(),
            EventId = wrapper.EventId,
            EventType = eventType,
            Topic = "product.events",
            Partition = 0,
            Offset = 42,
            MessageKey = "key-1",
            Payload = JsonSerializer.Serialize(wrapper),
            Headers = null,
            FirstFailureTime = DateTime.UtcNow.AddMinutes(-10),
            LastFailureTime = DateTime.UtcNow.AddMinutes(-5),
            RetryCount = retryCount,
            NextRetryAt = DateTime.UtcNow.AddMinutes(-1),
            Status = RetryRecordStatus.Pending,
            LastErrorMessage = "previous error",
            LastErrorType = "System.Exception",
            CorrelationId = null,
        };
    }

    private static IServiceProvider BuildServiceProvider(
        IRetryStore retryStore, params IProductEventConsumer[] consumers)
    {
        var sp = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        sp.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactory);
        scopeFactory.CreateScope().Returns(scope);

        scope.ServiceProvider.GetService(typeof(IRetryStore)).Returns(retryStore);
        scope.ServiceProvider.GetService(typeof(IEnumerable<IProductEventConsumer>))
            .Returns(consumers.AsEnumerable());

        return sp;
    }
}
