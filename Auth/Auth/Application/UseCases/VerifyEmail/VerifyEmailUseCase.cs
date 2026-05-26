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
        var code = await verificationTokenRepository.GetByCodeAsync(command.Code);

        if (code == null)
        {
            return new VerifyEmailResponse(false, "Invalid verification code", null);
        }

        if (code.IsUsed)
        {
            return new VerifyEmailResponse(false, "Verification code has already been used", null);
        }

        if (code.ExpiresAt < DateTime.UtcNow)
        {
            return new VerifyEmailResponse(false, "Verification code has expired", null);
        }

        var success = await userGateway.VerifyUserEmailAsync(code.UserId);

        if (!success)
        {
            return new VerifyEmailResponse(false, "Failed to verify email in user service", null);
        }

        await verificationTokenRepository.MarkAsUsedAsync(command.Code);

        return new VerifyEmailResponse(true, "Email verified successfully", code.UserId);
    }
}