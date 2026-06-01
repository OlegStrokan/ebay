using Application.Common.Interfaces;
using Domain.Common.Interfaces;
using Domain.Entities;
using Domain.Gateways;
using Domain.Repositories;

namespace Application.UseCases.Login;

public class LoginUseCase
(
    IRefreshTokenRepository refreshTokenRepository,
    IIdGenerator idGenerator,
    IUserGateway userGateway,
    IJwtTokenGenerator jwtTokenGenerator
) : ILoginUseCase
{
    
    public async Task<LoginResponse> ExecuteAsync(LoginCommand command)
    {
        var user = await userGateway.VerifyCredentialsAsync(command.Email, command.Password);

        if (user == null)
        {
            throw new UnauthorizedAccessException("Invalid email or password");
        }

        if (!user.IsEmailVerified) {
            throw new UnauthorizedAccessException("Email not verified");
        }

        if (user.Status == UserStatus.Banned)
        {
            throw new UnauthorizedAccessException("Your account has been banned");
        }


        var accessToken = jwtTokenGenerator.GenerateAccessToken(user.Id, user.Email, user.Roles, user.CompanyId);

        var refreshTokenValue = jwtTokenGenerator.GenerateRefreshToken();

        var refreshToken = new RefreshTokenEntity
        {
            Id = idGenerator.GenerateId(),
            UserId = user.Id,
            Token = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false
        };

        await refreshTokenRepository.CreateAsync(refreshToken);

        return new LoginResponse(accessToken, refreshTokenValue, 3600, "Bearer");
}
}