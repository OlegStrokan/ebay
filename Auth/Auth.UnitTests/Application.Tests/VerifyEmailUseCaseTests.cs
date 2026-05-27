using Application.UseCases.VerifyEmail;
using Domain.Entities;
using Domain.Gateways;
using Domain.Repositories;
using NSubstitute;

namespace Application.Tests;

public class VerifyEmailUseCaseTests
{
    [Fact]
    public async Task ShouldSuccessfullyVerifyEmail()
    {
        var emailVerificationRepository = Substitute.For<IEmailVerificationTokenRepository>();
        var userGateway = Substitute.For<IUserGateway>();

        var emailVerificationToken = new EmailVerificationTokenEntity
        {
            UserId = "userId",
            Id = "tokenId",
            IsUsed = false,
            Code = "123456",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        emailVerificationRepository.GetByCodeAsync(emailVerificationToken.Code).Returns(emailVerificationToken);
        userGateway.VerifyUserEmailAsync(emailVerificationToken.UserId).Returns(true);
        
        var command = new VerifyEmailCommand(emailVerificationToken.Code);
        
        var useCase = new VerifyEmailUseCase(emailVerificationRepository, userGateway);

        var result = await useCase.ExecuteAsync(command);
        
        Assert.Equal(emailVerificationToken.UserId, result.UserId);
        Assert.Equal("Email verified successfully", result.Message);
        Assert.True(result.Success);
        
        
        await emailVerificationRepository.Received(1).GetByCodeAsync(emailVerificationToken.Code);
        await userGateway.Received(1).VerifyUserEmailAsync(emailVerificationToken.UserId);
        
    }

    [Fact]
    public async Task ShouldReturnFailureWhenVerificationTokenNotFound()
    {
        var emailVerificationRepository = Substitute.For<IEmailVerificationTokenRepository>();
        var userGateway = Substitute.For<IUserGateway>();
        
        emailVerificationRepository.GetByCodeAsync(Arg.Any<string>()).Returns((EmailVerificationTokenEntity?)null);
        
        var command = new VerifyEmailCommand("123456");
        
        var useCase = new VerifyEmailUseCase(emailVerificationRepository, userGateway);

        var result = await useCase.ExecuteAsync(command);

        Assert.Equal("Invalid verification token", result.Message);
        Assert.False(result.Success);
        Assert.Null(result.UserId);
        
        await emailVerificationRepository.Received(1).GetByCodeAsync("123456");
        await userGateway.DidNotReceive().VerifyUserEmailAsync(Arg.Any<string>());
    }
    
    [Fact]
    public async Task ShouldReturnFailureWhenVerificationTokenAlreadyUsed()
    {
        var emailVerificationRepository = Substitute.For<IEmailVerificationTokenRepository>();
        var userGateway = Substitute.For<IUserGateway>();
        
        var emailVerificationToken = new EmailVerificationTokenEntity
        {
            UserId = "userId",
            Id = "tokenId",
            IsUsed = true,
            Code = "123456",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        emailVerificationRepository.GetByCodeAsync(emailVerificationToken.Code).Returns(emailVerificationToken);

        var command = new VerifyEmailCommand(emailVerificationToken.Code);
        
        var useCase = new VerifyEmailUseCase(emailVerificationRepository, userGateway);

        var result = await useCase.ExecuteAsync(command);

        Assert.Equal("Verification token has already been used", result.Message);
        Assert.False(result.Success);
        Assert.Null(result.UserId);
        
        await emailVerificationRepository.Received(1).GetByCodeAsync("123456");
        await userGateway.DidNotReceive().VerifyUserEmailAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ShouldReturnFailureWhenVerificationTokenExpired()
    {
        var emailVerificationRepository = Substitute.For<IEmailVerificationTokenRepository>();
        var userGateway = Substitute.For<IUserGateway>();
        
        var emailVerificationToken = new EmailVerificationTokenEntity
        {
            UserId = "userId",
            Id = "tokenId",
            IsUsed = false,
            Code = "123456",
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
        };
        emailVerificationRepository.GetByCodeAsync(emailVerificationToken.Code).Returns(emailVerificationToken);

        var command = new VerifyEmailCommand(emailVerificationToken.Code);
        
        var useCase = new VerifyEmailUseCase(emailVerificationRepository, userGateway);

        var result = await useCase.ExecuteAsync(command);

        Assert.Equal("Verification token has expired", result.Message);
        Assert.False(result.Success);
        Assert.Null(result.UserId);
        
        await emailVerificationRepository.Received(1).GetByCodeAsync("123456");
        await userGateway.DidNotReceive().VerifyUserEmailAsync(Arg.Any<string>());
    }
    
    [Fact]
    public async Task ShouldReturnFailureWheVerifyUserFailed()
    {
        var emailVerificationRepository = Substitute.For<IEmailVerificationTokenRepository>();
        var userGateway = Substitute.For<IUserGateway>();
        
        var emailVerificationToken = new EmailVerificationTokenEntity
        {
            UserId = "userId",
            Id = "tokenId",
            IsUsed = false,
            Code = "123456",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        emailVerificationRepository.GetByCodeAsync(emailVerificationToken.Code).Returns(emailVerificationToken);
        userGateway.VerifyUserEmailAsync(emailVerificationToken.UserId).Returns(false);
        
        var command = new VerifyEmailCommand(emailVerificationToken.Code);
        
        var useCase = new VerifyEmailUseCase(emailVerificationRepository, userGateway);

        var result = await useCase.ExecuteAsync(command);

        Assert.Equal("Failed to verify email in user service", result.Message);
        Assert.False(result.Success);
        Assert.Null(result.UserId);
        
        await emailVerificationRepository.Received(1).GetByCodeAsync("123456");
        await emailVerificationRepository.DidNotReceive().MarkAsUsedAsync("123456");
        await userGateway.Received().VerifyUserEmailAsync(emailVerificationToken.UserId);

    }
}