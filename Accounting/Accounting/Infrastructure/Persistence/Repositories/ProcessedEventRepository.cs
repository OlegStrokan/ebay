using Domain.Entities;
using Domain.Interfaces;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

internal sealed class ProcessedEventRepository(AccountingDbContext dbContext) : IProcessedEventRepository
{
    public Task<bool> ExistsAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return dbContext.ProcessedEvents.AnyAsync(x => x.EventId == eventId, cancellationToken);
    }

    public async Task AddAsync(ProcessedEvent processedEvent, CancellationToken cancellationToken = default)
    {
        await dbContext.ProcessedEvents.AddAsync(processedEvent, cancellationToken);
    }
}
