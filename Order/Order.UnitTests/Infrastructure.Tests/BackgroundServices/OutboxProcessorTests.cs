using Application.Interfaces;
using Application.Models;
using Domain.Events;
using Domain.Events.CreateOrder;
using Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Infrastructure.Tests.BackgroundServices;

public class OutboxProcessorTests
{
    private readonly IEventPublisher _eventPublisher = Substitute.For<IEventPublisher>();
    private readonly IOutboxRepository _outboxRepository = Substitute.For<IOutboxRepository>();
    private readonly IDeadLetterRepository _deadLetterRepository = Substitute.For<IDeadLetterRepository>();
    private readonly ILogger<OutboxProcessor> _logger = Substitute.For<ILogger<OutboxProcessor>>();

    // Real config so GetValue<int>(..., defaultValue) works correctly in the processor
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Outbox:BatchSize"]      = "20",
            ["Outbox:MaxRetries"]     = "5",
            ["Outbox:MaxAgeDays"]     = "7",
            ["Outbox:PollIntervalMs"] = "5000",
            ["Outbox:MaxParallelism"] = "5"
        })
        .Build();

    // scoping mocks
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IServiceScope _serviceScope = Substitute.For<IServiceScope>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();

    public OutboxProcessorTests()
    {
        _serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(_scopeFactory);
        _scopeFactory.CreateScope().Returns(_serviceScope);
        _serviceScope.ServiceProvider.GetService(typeof(IOutboxRepository)).Returns(_outboxRepository);
        // ProcessBatchAsync always resolves IDeadLetterRepository - must be registered
        _serviceScope.ServiceProvider.GetService(typeof(IDeadLetterRepository)).Returns(_deadLetterRepository);
        _serviceScope.ServiceProvider.GetService(typeof(IEventPublisher)).Returns(_eventPublisher);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPublishAndMarkAsProcessed_WhenMessagesExists()
    {
        var message = new OutboxMessage(Guid.NewGuid(), nameof(OrderCreatedEvent), "{}", DateTime.UtcNow, Guid.NewGuid().ToString());

        _outboxRepository.ClaimUnprocessedMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<OutboxMessage> { message });

        var signal = new TaskCompletionSource<bool>();

        _outboxRepository
            .When(x => x.MarkAsProcessedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = new OutboxProcessor(_serviceProvider, _logger, _configuration);
        using var cts = new CancellationTokenSource();

        await worker.StartAsync(cts.Token);

        var completedTask = await Task.WhenAny(signal.Task, Task.Delay(3000, cts.Token));
        Assert.True(completedTask == signal.Task, "MarkAsProcessedAsync was never called within 3 seconds.");
        await cts.CancelAsync();

        await _eventPublisher.Received(1).PublishRawAsync(
            message.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _outboxRepository.Received(1).MarkAsProcessedAsync(message.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIncrementRetryAndNotMarkProcessed_WhenPublishingFails()
    {
        var message = new OutboxMessage(Guid.NewGuid(), "BrokenEvent", "{}", DateTime.UtcNow, Guid.NewGuid().ToString());

        _outboxRepository.ClaimUnprocessedMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<OutboxMessage> { message });

        _eventPublisher.PublishRawAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Kafka is down!"));

        var signal = new TaskCompletionSource<bool>();

        _outboxRepository
            .When(x => x.IncrementRetryCountAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = new OutboxProcessor(_serviceProvider, _logger, _configuration);
        using var cts = new CancellationTokenSource();

        await worker.StartAsync(cts.Token);

        var completedTask = await Task.WhenAny(signal.Task, Task.Delay(3000, cts.Token));
        await cts.CancelAsync();

        Assert.True(completedTask == signal.Task, "IncrementRetryCountAsync was never called within 3 seconds.");
        await _outboxRepository.DidNotReceive().MarkAsProcessedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _outboxRepository.Received(1).IncrementRetryCountAsync(message.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMoveToDeadLetter_WhenRetryCountExceedsMax()
    {
        var message = new OutboxMessage(Guid.NewGuid(), nameof(OrderCreatedEvent), "{}", DateTime.UtcNow, Guid.NewGuid().ToString());
        // RetryCount has a private setter - use reflection to configure test state
        typeof(OutboxMessage).GetProperty(nameof(OutboxMessage.RetryCount))!
            .SetValue(message, 5);

        _outboxRepository.ClaimUnprocessedMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<OutboxMessage> { message });

        var signal = new TaskCompletionSource<bool>();

        _deadLetterRepository
            .When(x => x.AddAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = new OutboxProcessor(_serviceProvider, _logger, _configuration);
        using var cts = new CancellationTokenSource();

        await worker.StartAsync(cts.Token);

        var completedTask = await Task.WhenAny(signal.Task, Task.Delay(3000, cts.Token));
        await cts.CancelAsync();

        Assert.True(completedTask == signal.Task, "DeadLetterRepository.AddAsync was never called within 3 seconds.");
        await _eventPublisher.DidNotReceive().PublishRawAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _outboxRepository.Received(1).DeleteAsync(message.Id, Arg.Any<CancellationToken>());
    }
    
    [Fact]
    public async Task ExecuteAsync_ShouldMoveToDeadLetter_WhenMessageExceedsMaxAge()
    {
        var oldDate = DateTime.UtcNow.AddDays(-8); // 8 days old vs 7-day threshold
        var message = new OutboxMessage(Guid.NewGuid(), nameof(OrderCreatedEvent), "{}", oldDate, Guid.NewGuid().ToString());

        _outboxRepository.ClaimUnprocessedMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<OutboxMessage> { message });

        var signal = new TaskCompletionSource<bool>();

        _deadLetterRepository
            .When(x => x.AddAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult(true));

        var worker = new OutboxProcessor(_serviceProvider, _logger, _configuration);
        using var cts = new CancellationTokenSource();

        await worker.StartAsync(cts.Token);

        var completedTask = await Task.WhenAny(signal.Task, Task.Delay(3000, cts.Token));
        await cts.CancelAsync();

        Assert.True(completedTask == signal.Task, "DeadLetterRepository.AddAsync was never called within 3 seconds.");
        await _eventPublisher.DidNotReceive().PublishRawAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _outboxRepository.Received(1).DeleteAsync(message.Id, Arg.Any<CancellationToken>());
    }
}