using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.DbContext;

public sealed class AccountingDbContext(DbContextOptions<AccountingDbContext> options)
    : Microsoft.EntityFrameworkCore.DbContext(options)
{
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    public DbSet<FxRate> FxRates => Set<FxRate>();

    public DbSet<LedgerReportingEntry> LedgerReportingEntries => Set<LedgerReportingEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccountingDbContext).Assembly);
    }
}
