using Domain.Entities;
using Domain.Interfaces;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

internal sealed class FxRateRepository(AccountingDbContext dbContext) : IFxRateRepository
{
    public async Task<FxRate?> GetEffectiveRateAsync(
        string baseCurrency,
        string quoteCurrency,
        DateTime asOf,
        CancellationToken cancellationToken = default)
    {
        var basis = baseCurrency.Trim().ToUpperInvariant();
        var quote = quoteCurrency.Trim().ToUpperInvariant();

        return await dbContext.FxRates
            .Where(x => x.BaseCurrency == basis && x.QuoteCurrency == quote && x.EffectiveFrom <= asOf)
            .OrderByDescending(x => x.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<bool> ExistsAsync(
        string baseCurrency,
        string quoteCurrency,
        DateTime effectiveFrom,
        CancellationToken cancellationToken = default)
    {
        var basis = baseCurrency.Trim().ToUpperInvariant();
        var quote = quoteCurrency.Trim().ToUpperInvariant();

        return dbContext.FxRates.AnyAsync(
            x => x.BaseCurrency == basis && x.QuoteCurrency == quote && x.EffectiveFrom == effectiveFrom,
            cancellationToken);
    }

    public async Task AddAsync(FxRate rate, CancellationToken cancellationToken = default)
    {
        await dbContext.FxRates.AddAsync(rate, cancellationToken);
    }
}
