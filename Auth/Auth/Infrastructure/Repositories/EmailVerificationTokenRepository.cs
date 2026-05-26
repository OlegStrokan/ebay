using Domain.Entities;
using Domain.Repositories;
using Infrastructure.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class EmailVerificationTokenRepository(AppDbContext dbContext): IEmailVerificationTokenRepository
{
    public async Task<EmailVerificationTokenEntity?> GetByCodeAsync(string code)
    {
        return await dbContext.EmailVerificationTokens.FirstOrDefaultAsync(evt => evt.Code == code);
    }

    public async Task<EmailVerificationTokenEntity?> GetByUserIdAsync(string userId)
    {
        return await dbContext.EmailVerificationTokens.Where(evt =>
            evt.UserId == userId &&
            !evt.IsUsed &&
            evt.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(evt => evt.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<EmailVerificationTokenEntity> CreateAsync(EmailVerificationTokenEntity verificationTokenEntity)
    {
        dbContext.Add(verificationTokenEntity);
        await dbContext.SaveChangesAsync();
        return verificationTokenEntity;
    }

    public async Task<EmailVerificationTokenEntity> UpdateAsync(EmailVerificationTokenEntity verificationTokenEntity)
    {
        dbContext.Update(verificationTokenEntity);
        await dbContext.SaveChangesAsync();
        return verificationTokenEntity;
    }

    public async Task MarkAsUsedAsync(string code)
    {
        var emailVerificationToken = await GetByCodeAsync(code);

        if (emailVerificationToken == null)
        {
            throw new InvalidOperationException($"Email verification code {code} not found");
        }

        emailVerificationToken.IsUsed = true;
        emailVerificationToken.UsedAt = DateTime.UtcNow;
        
        await UpdateAsync(emailVerificationToken);
        
    }

    public async Task DeletedExpiredTokensAsync()
    {
        var expiredTokens = await dbContext.EmailVerificationTokens.Where(evt =>
            evt.ExpiresAt < DateTime.UtcNow
            || evt.IsUsed
        ).ToListAsync();
        
        dbContext.RemoveRange(expiredTokens);
        await dbContext.SaveChangesAsync();
        
        
        // code below should be used on production, but for testing it's wont work because we use InMemory database
        // await dbContext.Database.ExecuteSqlInterpolatedAsync(
        //     $"DELETE FROM EmailVerificationToken WHERE ExpiresAt < {DateTime.UtcNow} Or IsUsed = {true}");
    }

    public async Task DeleteByUserIdAsync(string userId)
    {
        var tokens = await dbContext.EmailVerificationTokens
            .Where(evt => evt.UserId == userId)
            .ToListAsync();
        
        dbContext.RemoveRange(tokens);
        await dbContext.SaveChangesAsync();
    }
}