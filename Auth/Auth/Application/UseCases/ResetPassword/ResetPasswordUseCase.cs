using Application.UseCases.RequestPasswordReset;
using Domain.Gateways;
using Domain.Repositories;

namespace Application.UseCases.ResetPassword;

public class ResetPasswordUseCase(
    IPasswordResetTokenRepository passwordResetTokenRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IUserGateway userGateway) : IResetPasswordUseCase
{
    public async Task<ResetPasswordResponse> ExecuteAsync(ResetPasswordCommand command)
    {
        var token = await passwordResetTokenRepository.GetByTokenAsync(command.Token);

        if (token == null)
        {
            return new ResetPasswordResponse(false, "Invalid reset token");
        }

        if (token.IsUsed)
        {
            return new ResetPasswordResponse(false, "Token has already been used");
        }

        if (token.ExpiresAt < DateTime.UtcNow)
        {
            return new ResetPasswordResponse(false, "Token has expired");
        }

        var success = await userGateway.UpdateUserPasswordAsync(token.UserId, command.NewPassword);

        if (!success)
        {
            return new ResetPasswordResponse(false, "Failed to update password in user service");
        }

        await passwordResetTokenRepository.MarkAsUsedAsync(command.Token);

        await refreshTokenRepository.RevokeAllUserTokensAsync(token.UserId);

        return new ResetPasswordResponse(true, "Password reset successfully");
    }
}