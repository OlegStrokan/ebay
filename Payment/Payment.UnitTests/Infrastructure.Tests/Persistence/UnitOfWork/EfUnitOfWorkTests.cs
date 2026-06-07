using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
}
