using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence.UnitOfWork;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Persistence.UnitOfWork;

public class EfUnitOfWorkTests
{
    [Fact]
    public async Task SaveChangesAsync_ShouldPersistChanges_AndReturnAffectedRows()
    {
        await using var context = Persistence.TestDbContextFactory.Create();
        var unitOfWork = new EfUnitOfWork(context);

        var payment = Payment.Create(
            PaymentId.From("pay-uow-1"),
            "order-uow-1",
            "customer-uow-1",
            Money.Create(99m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-uow-1"));

        await context.Payments.AddAsync(payment);

        var affected = await unitOfWork.SaveChangesAsync();

        Assert.True(affected > 0);
        Assert.Equal(1, await context.Payments.CountAsync());
    }

    [Fact]
    public async Task ClearTrackedChanges_ShouldDetachPendingEntities()
    {
        await using var context = Persistence.TestDbContextFactory.Create();
        var unitOfWork = new EfUnitOfWork(context);

        var payment = Payment.Create(
            PaymentId.From("pay-uow-2"),
            "order-uow-2",
            "customer-uow-2",
            Money.Create(50m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-uow-2"));

        await context.Payments.AddAsync(payment);

        Assert.Equal(EntityState.Added, context.Entry(payment).State);

        unitOfWork.ClearTrackedChanges();

        Assert.Equal(EntityState.Detached, context.Entry(payment).State);
    }

    [Fact]
    public async Task DetachUncommittedChanges_ShouldDetachDirtyEntities_AndLeaveUnchangedEntitiesTracked()
    {
        await using var context = Persistence.TestDbContextFactory.Create();
        var unitOfWork = new EfUnitOfWork(context);

        // Persist one payment so it becomes Unchanged in the tracker (simulates a batch item)
        var batchPayment = Payment.Create(
            PaymentId.From("pay-uow-batch"),
            "order-uow-batch",
            "customer-uow-batch",
            Money.Create(50m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-uow-batch"));
        await context.Payments.AddAsync(batchPayment);
        await unitOfWork.SaveChangesAsync();

        Assert.Equal(EntityState.Unchanged, context.Entry(batchPayment).State);

        // Simulate a second entity being added (e.g., OutboundOrderCallback for the failing item)
        var failingPayment = Payment.Create(
            PaymentId.From("pay-uow-fail"),
            "order-uow-fail",
            "customer-uow-fail",
            Money.Create(75m, "USD"),
            PaymentMethod.Card,
            IdempotencyKey.From("idem-uow-fail"));
        await context.Payments.AddAsync(failingPayment);

        Assert.Equal(EntityState.Added, context.Entry(failingPayment).State);

        unitOfWork.DetachUncommittedChanges();

        // Failing item's pending changes are gone
        Assert.Equal(EntityState.Detached, context.Entry(failingPayment).State);

        // Batch item (Unchanged) is still tracked — no re-attachment needed on next iteration
        Assert.Equal(EntityState.Unchanged, context.Entry(batchPayment).State);
    }
}
