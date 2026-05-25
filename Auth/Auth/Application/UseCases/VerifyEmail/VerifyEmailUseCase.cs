using Domain.Gateways;
using Domain.Repositories;

namespace Application.UseCases.VerifyEmail;

public class VerifyEmailUseCase (
    IEmailVerificationTokenRepository verificationTokenRepository,
    IUserGateway userGateway
    ) : IVerifyEmailUseCase
{
    public async Task<VerifyEmailResponse> ExecuteAsync(VerifyEmailCommand command)
    {
        var token = await verificationTokenRepository.GetByTokenAsync(command.Code);

        if (token == null)
        {
            return new VerifyEmailResponse(false, "Invalid verification token", null);
        }

        if (token.IsUsed)
        {
            return new VerifyEmailResponse(false, "Token has already been used", null);
        }

        if (token.ExpiresAt < DateTime.UtcNow)
        {
            return new VerifyEmailResponse(false, "Token has expired", null);
        }

        var success = await userGateway.VerifyUserEmailAsync(token.UserId);

        if (!success)
        {
            return new VerifyEmailResponse(false, "Failed to verify email in user service", null);
        }

        await verificationTokenRepository.MarkAsUsedAsync(command.Code);

        return new VerifyEmailResponse(true, "Email verified successfully", token.UserId);
    }
}