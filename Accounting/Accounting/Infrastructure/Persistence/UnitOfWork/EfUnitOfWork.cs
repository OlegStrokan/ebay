using Application.Interfaces;
using Domain.Exceptions;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Persistence.UnitOfWork;

internal sealed class EfUnitOfWork(AccountingDbContext dbContext) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, out var constraintName))
        {
            dbContext.ChangeTracker.Clear();
            throw new DuplicateLedgerTransactionException(constraintName ?? "unknown", ex);
        }
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
