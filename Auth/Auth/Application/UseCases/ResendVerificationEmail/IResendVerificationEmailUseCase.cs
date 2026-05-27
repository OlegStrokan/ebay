namespace Application.UseCases.ResendVerificationEmail;

public interface IResendVerificationEmailUseCase
{
    Task<ResendVerificationEmailResponse> ExecuteAsync(ResendVerificationEmailCommand command);
}
