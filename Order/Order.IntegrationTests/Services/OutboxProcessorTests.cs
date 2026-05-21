using Application.Interfaces;
using Application.Models;
using FluentAssertions;
using Infrastructure.BackgroundServices;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Order.IntegrationTests.Infrastructure;
using Order.IntegrationTests.TestHelpers;
using Xunit;

namespace Order.IntegrationTests.Services;

// kafka is mocked here
[Collection("Integration")]
public sealed class OutboxProcessorTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public OutboxProcessorTests(IntegrationFixture fixture) => _fixture = fixture;
    
    private (OutboxProcessor Processor, FakeEventPublisher Publisher) CreateProcessor(
        int maxRetries = 5)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:PollIntervalMs"]  = "50",
                ["Outbox:BatchSize"]       = "20",
                ["Outbox:MaxRetries"]      = maxRetries.ToString(),
                ["Outbox:MaxAgeDays"]      = "7",
                ["Outbox:MaxParallelism"]  = "1"
            })
            .Build();

        var publisher = (FakeEventPublisher)_fixture.Services.GetRequiredService<IEventPublisher>();

        var processor = new OutboxProcessor(
            _fixture.Services,
            NullLogger<OutboxProcessor>.Instance,
            config);

        return (processor, publisher);
    }
    
    [Fact]
    public async Task OutboxProcessor_ShouldPublishPendingMessage_AndMarkItProcessed()
    {
        // seed one unprocessed outbox message before starting the processor
        var messageId = Guid.NewGuid();

        await using (var setupScope = _fixture.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OutboxMessages.Add(new OutboxMessage(messageId, "OrderCreatedEvent", "{}", DateTime.UtcNow, messageId.ToString()));
            await db.SaveChangesAsync();
        }

        var (processor, publisher) = CreateProcessor();

        // run the background service long enough for at least one batch
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        await processor.StopAsync(CancellationToken.None);

        // publisher received the call
        publisher.Published.Should()
            .Contain(p => p.Id == messageId,
                "the processor must deliver every unprocessed outbox message to IEventPublisher");

        // DB row is now marked as processed
        await using var assertScope = _fixture.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await assertDb.OutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId);
        row.Should().NotBeNull();
        row!.ProcessedOnUtc.Should().NotBeNull("MarkAsProcessedAsync must set ProcessedOnUtc");
    }


    [Fact]
    public async Task OutboxProcessor_ShouldMoveToDeadLetter_WhenRetryLimitReached()
    {
        // seed a message whose RetryCount already equals maxRetries
        const int maxRetries = 3;
        var messageId = Guid.NewGuid();

        await using (var setupScope = _fixture.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();

            var msg = new OutboxMessage(messageId, "SomeEvent", "{}", DateTime.UtcNow, messageId.ToString());
            for (var i = 0; i < maxRetries; i++)
                msg.UpdateFailure("previous error", DateTime.UtcNow);

            db.OutboxMessages.Add(msg);
            await db.SaveChangesAsync();
        }

        var (processor, publisher) = CreateProcessor(maxRetries: maxRetries);
        
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        await processor.StopAsync(CancellationToken.None);

        // message is gone from OutboxMessages
        await using var assertScope = _fixture.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var outboxRow = await assertDb.OutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId);
        outboxRow.Should().BeNull("processor must delete the outbox row once it is dead-lettered");

        // message now lives in DeadLetterMessages
        var deadRow = await assertDb.DeadLetterMessages.FirstOrDefaultAsync(m => m.Id == messageId);
        deadRow.Should().NotBeNull("processor must move the message to the dead-letter table");
        deadRow!.RetryCount.Should().Be(maxRetries);

        // publisher was NOT called (dead-lettered before publish attempt)
        publisher.Published.Should().NotContain(p => p.Id == messageId);
    }
    
    [Fact]
    public async Task OutboxProcessor_ShouldNotPublishAlreadyProcessedMessage()
    {
        // seed a message that was processed before the processor starts
        var messageId = Guid.NewGuid();

        await using (var setupScope = _fixture.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();

            var msg = new OutboxMessage(messageId, "OrderPaidEvent", "{}", DateTime.UtcNow, messageId.ToString());
            msg.MarkAsProcessed(DateTime.UtcNow);
            db.OutboxMessages.Add(msg);
            await db.SaveChangesAsync();
        }

        var (processor, publisher) = CreateProcessor();
        
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        await processor.StopAsync(CancellationToken.None);

        // GetUnprocessedMessagesAsync filters out rows with ProcessedOnUtc != null
        publisher.Published.Should().NotContain(p => p.Id == messageId,
            "already-processed messages must be excluded from the outbox query");
    }
}
