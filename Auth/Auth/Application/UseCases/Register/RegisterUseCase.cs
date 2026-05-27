using Application.Common.Interfaces;
using Domain.Common.Interfaces;
using Domain.Entities;
using Domain.Gateways;
using Domain.Repositories;

namespace Application.UseCases.Register;

public class RegisterUseCase(
    IEmailVerificationTokenRepository verificationTokenRepository, 
    IIdGenerator idGenerator,
    IUserGateway userGateway,
    IEmailGateway emailGateway
    ) : IRegisterUseCase
{

    public const string SuccessMessage = "User registered successfully. Please verify your email";

    public async Task<RegisterResponse> ExecuteAsync(RegisterCommand command)
    {
        var userId = await userGateway.CreateUserAsync(
            email: command.Email,
            hashedPassword: command.Password,
            fullName: command.Fullname,
            phone: command.Phone
        );


        var verificationCode = new EmailVerificationTokenEntity
        {
            Id = idGenerator.GenerateId(),
            UserId = userId,
            Code = Guid.NewGuid().ToString(),
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        };

        await verificationTokenRepository.CreateAsync(verificationCode);

        try
        {
            await emailGateway.SendVerificationEmailAsync(command.Email, verificationCode.Code);
        }
        catch (Exception)
        {
            // Email dispatch is non-critical; Kafka error already logged by EmailGateway
        }

        return new RegisterResponse(userId, command.Email, command.Fullname, SuccessMessage);
    }
}