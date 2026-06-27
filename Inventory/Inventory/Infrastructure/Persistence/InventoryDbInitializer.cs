using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Persistence;

public sealed class InventoryDbInitializer(
    IDbContextFactory<InventoryDbContext> dbContextFactory,
    ILogger<InventoryDbInitializer> logger)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Inventory database migrations applied.");
    }
}
