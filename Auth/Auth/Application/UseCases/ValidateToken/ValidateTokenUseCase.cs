using Application.Common.Interfaces;

namespace Application.UseCases.ValidateToken;

public class ValidateTokenUseCase(IJwtTokenValidator jwtTokenValidator) : IValidateTokenUseCase
{
    public Task<ValidateTokenResponse> ExecuteAsync(ValidateTokenCommand command)
    {
        var validationResult = jwtTokenValidator.ValidateToken(command.AccessToken);

        if (!validationResult.IsValid)
        {
            return Task.FromResult(new ValidateTokenResponse(false, null, [], "Invalid or expired token"));
        }

        return Task.FromResult(new ValidateTokenResponse(true, validationResult.UserId, validationResult.Roles, null));
    }
}