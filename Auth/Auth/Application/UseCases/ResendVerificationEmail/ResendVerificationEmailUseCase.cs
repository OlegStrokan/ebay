using Application.Common.Interfaces;
using Domain.Common.Interfaces;
using Domain.Entities;
using Domain.Gateways;
using Domain.Repositories;

namespace Application.UseCases.ResendVerificationEmail;

public class ResendVerificationEmailUseCase(
    IEmailVerificationTokenRepository verificationTokenRepository,
    IUserGateway userGateway,
    IEmailGateway emailGateway,
    IIdGenerator idGenerator) : IResendVerificationEmailUseCase
{
    public async Task<ResendVerificationEmailResponse> ExecuteAsync(ResendVerificationEmailCommand command)
    {
        var user = await userGateway.GetUserByEmailAsync(command.Email);

        // Do not reveal whether the email exists - always return success to the caller
        if (user == null)
            return new ResendVerificationEmailResponse(true, "If your email is registered, a verification link has been sent");

        if (user.IsEmailVerified)
            return new ResendVerificationEmailResponse(false, "Email is already verified");

        // Delete any existing tokens so the DB unique index on Code doesn't block the new one
        await verificationTokenRepository.DeleteByUserIdAsync(user.Id);

        var newToken = new EmailVerificationTokenEntity
        {
            Id = idGenerator.GenerateId(),
            UserId = user.Id,
            Code = Guid.NewGuid().ToString(),
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        };

        await verificationTokenRepository.CreateAsync(newToken);

        try
        {
            await emailGateway.SendVerificationEmailAsync(command.Email, newToken.Code);
        }
        catch (Exception)
        {
            // email dispatch is non-critical, kafka error already logged by EmailGateway
        }

        return new ResendVerificationEmailResponse(true, "If your email is registered, a verification link has been sent");
    }
}
