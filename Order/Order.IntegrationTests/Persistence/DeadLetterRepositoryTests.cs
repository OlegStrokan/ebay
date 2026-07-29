using Domain.Entities;
using Infrastructure.Persistence.DbContext;
using Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Order.IntegrationTests.Infrastructure;
using Xunit;

namespace Order.IntegrationTests.Persistence;

[Collection("Integration")]
public sealed class DeadLetterRepositoryTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public DeadLetterRepositoryTests(IntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RetryAsync_ShouldResolveOriginalRow_SoItCannotBeRetriedTwice()
    {
        var messageId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid().ToString();

        await using (var setupScope = _fixture.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var deadLetter = DeadLetterMessage.Create(
                messageId,
                "OrderCreatedEvent",
                "{}",
                DateTime.UtcNow.AddDays(-10),
                "boom",
                5,
                aggregateId);

            await setupDb.DeadLetterMessages.AddAsync(deadLetter);
            await setupDb.SaveChangesAsync();
        }

        await using (var firstScope = _fixture.CreateScope())
        {
            var db = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = CreateRepository(db);

            await repository.RetryAsync(messageId, CancellationToken.None);
        }

        await using var verifyScope = _fixture.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repositoryForSecondAttempt = CreateRepository(verifyDb);

        var stored = await verifyDb.DeadLetterMessages.SingleAsync(d => d.Id == messageId);
        Assert.True(stored.IsResolved);

        var outboxRows = await verifyDb.OutboxMessages
            .Where(o => o.AggregateId == aggregateId)
            .ToListAsync();
        Assert.Single(outboxRows);

        // second click on an already-resolved row must not publish a duplicate message
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repositoryForSecondAttempt.RetryAsync(messageId, CancellationToken.None));

        var outboxRowsAfterSecondAttempt = await verifyDb.OutboxMessages
            .Where(o => o.AggregateId == aggregateId)
            .ToListAsync();
        Assert.Single(outboxRowsAfterSecondAttempt);
    }

    [Fact]
    public async Task RetryAsync_ShouldResetOccurredOn_SoOutboxProcessorDoesNotImmediatelyReDeadLetter()
    {
        var messageId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid().ToString();
        var staleOccurredOn = DateTime.UtcNow.AddDays(-30);

        await using (var setupScope = _fixture.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var deadLetter = DeadLetterMessage.Create(
                messageId,
                "OrderCreatedEvent",
                "{}",
                staleOccurredOn,
                "exceeded max age",
                5,
                aggregateId);

            await setupDb.DeadLetterMessages.AddAsync(deadLetter);
            await setupDb.SaveChangesAsync();
        }

        var before = DateTime.UtcNow;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = CreateRepository(db);

            await repository.RetryAsync(messageId, CancellationToken.None);
        }

        await using var verifyScope = _fixture.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var requeued = await verifyDb.OutboxMessages.SingleAsync(o => o.AggregateId == aggregateId);
        Assert.True(requeued.OccurredOnUtc >= before);
    }

    private static DeadLetterRepository CreateRepository(AppDbContext db)
    {
        return new DeadLetterRepository(db, NullLogger<DeadLetterRepository>.Instance);
    }
}
