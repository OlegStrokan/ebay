namespace Application.Common.Interfaces;

public interface IEmailGateway
{
    Task SendVerificationEmailAsync(string recipientEmail, string verificationToken, CancellationToken cancellationToken = default);
    Task SendPasswordResetEmailAsync(string recipientEmail, string resetToken, CancellationToken cancellationToken = default);
}
