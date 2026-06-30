using Domain.Exceptions;
using Application.Interfaces;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Persistence.UnitOfWork;

internal sealed class EfUnitOfWork(PaymentDbContext dbContext) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, out var constraintName))
        {
            throw new UniqueConstraintViolationException(constraintName, ex);
        }
    }

    public void ClearTrackedChanges()
    {
        dbContext.ChangeTracker.Clear();
    }

    public void DetachUncommittedChanges()
    {
        var dirtyEntries = dbContext.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Modified or EntityState.Added or EntityState.Deleted)
            .ToList();

        foreach (var entry in dirtyEntries)
            entry.State = EntityState.Detached;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex, out string? constraintName)
    {
        if (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            constraintName = pg.ConstraintName;
            return true;
        }

        constraintName = null;
        return false;
    }
}