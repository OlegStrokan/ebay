using Application.Models;

namespace Application.Interfaces;

public interface IAdminActionAuditRepository
{
    Task RecordAsync(AdminActionAuditEntry entry, CancellationToken cancellationToken);
}
