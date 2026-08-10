using Application.Interfaces;
using Application.Models;
using Infrastructure.Persistence.DbContext;

namespace Infrastructure.Persistence.Repositories;

public sealed class AdminActionAuditRepository(AppDbContext dbContext) : IAdminActionAuditRepository
{
    public async Task RecordAsync(AdminActionAuditEntry entry, CancellationToken cancellationToken)
    {
        await dbContext.AdminActionAuditEntries.AddAsync(entry, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
