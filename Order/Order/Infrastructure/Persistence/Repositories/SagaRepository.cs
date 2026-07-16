using System.Data;
using Application.Sagas;
using Application.Sagas.Persistence;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public class SagaRepository(AppDbContext dbContext) : ISagaRepository
{
    public async Task<SagaState?> GetByIdAsync(Guid sagaId, CancellationToken cancellationToken)
    {
        return await dbContext.SagaStates
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == sagaId, cancellationToken);
    }

    public async Task<SagaState?> GetByCorrelationIdAsync(Guid correlationId, string sagaType, CancellationToken cancellationToken)
    {
        return await dbContext.SagaStates
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x =>
                    x.CorrelationId == correlationId &&
                    x.SagaType == sagaType,
                cancellationToken);
    }

    public async Task SaveAsync(SagaState sagaState, CancellationToken cancellationToken)
    {
        var existing = await dbContext.SagaStates.AnyAsync(x => x.Id == sagaState.Id, cancellationToken);

        if (!existing)
        {
            await dbContext.SagaStates.AddAsync(sagaState, cancellationToken);
        }
        else
        {
            dbContext.SagaStates.Update(sagaState);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveStepAsync(SagaStepLog stepLog, CancellationToken cancellationToken)
    {
        var existing = await dbContext.SagaStepLogs
            .AnyAsync(x => x.Id == stepLog.Id, cancellationToken);

        if (!existing)
        {
            await dbContext.SagaStepLogs.AddAsync(stepLog, cancellationToken);
        }
        else
        {
            dbContext.SagaStepLogs.Update(stepLog);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<SagaStepLog>> GetStepLogsAsync(Guid sagaId, CancellationToken cancellationToken)
    {
        return await dbContext.SagaStepLogs
            .Where(s => s.SagaId == sagaId)
            .OrderBy(s => s.StartedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<SagaState>> GetStuckSagasAsync(DateTime updatedBeforeCutoff, CancellationToken cancellationToken)
    {
        return await dbContext.SagaStates
            .Where(x => (x.Status == SagaStatus.Running || x.Status == SagaStatus.TimedOut)
                        && x.UpdatedAt < updatedBeforeCutoff)
            .Include(x => x.Steps)
            .ToListAsync(cancellationToken);
    }

    public async Task<(List<SagaState> Items, int TotalCount)> GetSagasAsync(
        string? status,
        string? sagaType,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SagaStates.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SagaStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(x => x.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(sagaType))
        {
            query = query.Where(x => x.SagaType == sagaType);
        }

        if (!string.IsNullOrWhiteSpace(search) && Guid.TryParse(search, out var searchId))
        {
            query = query.Where(x => x.Id == searchId || x.CorrelationId == searchId);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task SaveCompensationStateAsync(
        SagaState sagaState, 
        SagaStepLog stepLog,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            try
            {
                dbContext.SagaStepLogs.Update(stepLog);
                dbContext.SagaStates.Update(sagaState);

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }
}