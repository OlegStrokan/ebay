using Application.Common.Interfaces;
using Application.UseCases.ResendVerificationEmail;
using Domain.Common.Interfaces;
using Domain.Entities;
using Domain.Gateways;
using Domain.Repositories;
using NSubstitute;

namespace Application.Tests;

public class ResendVerificationEmailUseCaseTests
{
    private static bool IsValidGuid(string value) => Guid.TryParse(value, out _);
    [Fact]
    public async Task ShouldResendVerificationEmail_WhenUserExistsAndNotVerified()
    {
        var userGateway = Substitute.For<IUserGateway>();
        var repo = Substitute.For<IEmailVerificationTokenRepository>();
        var emailGateway = Substitute.For<IEmailGateway>();
        var idGenerator = Substitute.For<IIdGenerator>();

        idGenerator.GenerateId().Returns("new-token-id");
        userGateway.GetUserByEmailAsync("user@example.com").Returns(new UserGatewayDto
        {
            Id = "userId",
            Email = "user@example.com",
            FullName = "John Doe",
            Phone = "+1234567890",
            IsEmailVerified = false
        });

        var useCase = new ResendVerificationEmailUseCase(repo, userGateway, emailGateway, idGenerator);
        var result = await useCase.ExecuteAsync(new ResendVerificationEmailCommand("user@example.com"));

        Assert.True(result.Success);
        await repo.Received(1).DeleteByUserIdAsync("userId");
        await repo.Received(1).CreateAsync(Arg.Is<EmailVerificationTokenEntity>(t =>
            t.UserId == "userId" && IsValidGuid(t.Code) && !t.IsUsed));
        await emailGateway.Received(1).SendVerificationEmailAsync("user@example.com", Arg.Any<string>());
    }

    [Fact]
    public async Task ShouldReturnSuccess_WhenUserNotFound_WithoutRevealingExistence()
    {
        var userGateway = Substitute.For<IUserGateway>();
        var repo = Substitute.For<IEmailVerificationTokenRepository>();
        var emailGateway = Substitute.For<IEmailGateway>();
        var idGenerator = Substitute.For<IIdGenerator>();

        userGateway.GetUserByEmailAsync(Arg.Any<string>()).Returns((UserGatewayDto?)null);

        var useCase = new ResendVerificationEmailUseCase(repo, userGateway, emailGateway, idGenerator);
        var result = await useCase.ExecuteAsync(new ResendVerificationEmailCommand("ghost@example.com"));

        Assert.True(result.Success);
        await repo.DidNotReceive().CreateAsync(Arg.Any<EmailVerificationTokenEntity>());
        await emailGateway.DidNotReceive().SendVerificationEmailAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ShouldReturnFailure_WhenEmailAlreadyVerified()
    {
        var userGateway = Substitute.For<IUserGateway>();
        var repo = Substitute.For<IEmailVerificationTokenRepository>();
        var emailGateway = Substitute.For<IEmailGateway>();
        var idGenerator = Substitute.For<IIdGenerator>();

        userGateway.GetUserByEmailAsync("verified@example.com").Returns(new UserGatewayDto
        {
            Id = "userId",
            Email = "verified@example.com",
            FullName = "Jane Doe",
            Phone = "+1234567890",
            IsEmailVerified = true
        });

        var useCase = new ResendVerificationEmailUseCase(repo, userGateway, emailGateway, idGenerator);
        var result = await useCase.ExecuteAsync(new ResendVerificationEmailCommand("verified@example.com"));

        Assert.False(result.Success);
        Assert.Equal("Email is already verified", result.Message);
        await emailGateway.DidNotReceive().SendVerificationEmailAsync(Arg.Any<string>(), Arg.Any<string>());
    }
}
