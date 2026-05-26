using Domain.Entities;

namespace Domain.Repositories;

public interface IEmailVerificationTokenRepository
{
    Task<EmailVerificationTokenEntity?> GetByCodeAsync(string code);
    Task<EmailVerificationTokenEntity?> GetByUserIdAsync(string userId);
    Task<EmailVerificationTokenEntity> CreateAsync(EmailVerificationTokenEntity verificationTokenEntity);
    Task<EmailVerificationTokenEntity> UpdateAsync(EmailVerificationTokenEntity verificationTokenEntity);
    Task MarkAsUsedAsync(string code);
    Task DeletedExpiredTokensAsync();
    Task DeleteByUserIdAsync(string userId); 
}