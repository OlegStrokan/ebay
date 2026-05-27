using Application.Common.Interfaces;
using Application.UseCases.Register;
using Domain.Common.Interfaces;
using Domain.Entities;
using Domain.Gateways;
using Domain.Repositories;
using NSubstitute;

namespace Application.Tests;

public class RegisterUseCaseTests
{
    private static bool IsValidGuid(string value) => Guid.TryParse(value, out _);
    [Fact]
    public async Task ShouldRegisterUserAndCreateVerificationToken()
    {
        var userGateway = Substitute.For<IUserGateway>();
        var idGenerator = Substitute.For<IIdGenerator>();
        var emailVerificationTokenRepository = Substitute.For<IEmailVerificationTokenRepository>();
        var emailGateway = Substitute.For<IEmailGateway>();
        var generatedUserId = "ULID_GENERATED_USER";
        var generatedTokenId = "ULID_GENERATED_TOKEN";

        idGenerator.GenerateId().Returns(generatedTokenId);
         userGateway.CreateUserAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>()
            ).Returns(generatedUserId);

            var command = new RegisterCommand(
                "test@example.com",
                "password123",
                "John Doe",
                "+1234567890");

            var useCase = new RegisterUseCase(emailVerificationTokenRepository, idGenerator, userGateway, emailGateway);

            var result = await useCase.ExecuteAsync(command);
            
            Assert.Equal(generatedUserId, result.UserId);
            Assert.Equal(command.Email, result.Email);
            Assert.Equal(command.Fullname, result.Fullname);
            Assert.Equal(RegisterUseCase.SuccessMessage, result.Message);
            
            await userGateway.Received(1).CreateUserAsync(command.Email, command.Password, command.Fullname, command.Phone);

            await emailVerificationTokenRepository.Received(1).CreateAsync(
                Arg.Is<EmailVerificationTokenEntity>(t =>
                    t.Id == generatedTokenId &&
                    t.UserId == generatedUserId &&
                    IsValidGuid(t.Code) &&
                    t.ExpiresAt > DateTime.UtcNow.AddHours(23) &&
                    t.IsUsed == false
                ));

            await emailGateway.Received(1).SendVerificationEmailAsync(command.Email,
                Arg.Is<string>(c => IsValidGuid(c)));
    }
}