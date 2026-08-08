using Application.UseCases.UpdateUserPassword;
using Domain.Common.Interfaces;
using Domain.Entities.User;
using Domain.Repositories;
using NSubstitute;

namespace Application.Tests;

public class UpdateUserPasswordUseCaseTests
{
    [Fact]
    public async Task ShouldUpdatePassword_WhenUserExists()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var passwordHasher = Substitute.For<IPasswordHasher>();
        passwordHasher.HashPassword("new-password").Returns("new-hash");

        var user = new UserEntity
        {
            Id = "user-id",
            Email = "test@example.com",
            Password = "old-hash",
            Fullname = "John Doe",
            Phone = "+1234567890",
            CountryCode = "DE",
            CustomerTier = CustomerTier.Standard,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        userRepository.GetUserById(user.Id).Returns(user);
        userRepository.UpdateUser(Arg.Any<UserEntity>()).Returns(user);

        var useCase = new UpdateUserPasswordUseCase(userRepository, passwordHasher);

        var result = await useCase.ExecuteAsync(new UpdateUserPasswordCommand(user.Id, "new-password"));

        Assert.True(result.Success);
        Assert.Equal("Password updated successfully", result.Message);
        // The plaintext must never be persisted — hashing moved here from Auth.
        Assert.Equal("new-hash", user.Password);

        passwordHasher.Received(1).HashPassword("new-password");
        await userRepository.Received(1).UpdateUser(Arg.Is<UserEntity>(u => u.Id == user.Id && u.Password == "new-hash"));
    }

    [Fact]
    public async Task ShouldReturnFailure_WhenUserNotFound()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var passwordHasher = Substitute.For<IPasswordHasher>();
        userRepository.GetUserById("missing-id").Returns((UserEntity?)null);

        var useCase = new UpdateUserPasswordUseCase(userRepository, passwordHasher);

        var result = await useCase.ExecuteAsync(new UpdateUserPasswordCommand("missing-id", "new-password"));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
        await userRepository.DidNotReceive().UpdateUser(Arg.Any<UserEntity>());
    }

    [Fact]
    public async Task ShouldThrowArgumentException_WhenUserIdIsEmpty()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var passwordHasher = Substitute.For<IPasswordHasher>();
        var useCase = new UpdateUserPasswordUseCase(userRepository, passwordHasher);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => useCase.ExecuteAsync(new UpdateUserPasswordCommand("  ", "new-password")));

        Assert.Equal("UserId", exception.ParamName);
    }

    [Fact]
    public async Task ShouldThrowArgumentException_WhenNewPasswordIsEmpty()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var passwordHasher = Substitute.For<IPasswordHasher>();
        var useCase = new UpdateUserPasswordUseCase(userRepository, passwordHasher);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => useCase.ExecuteAsync(new UpdateUserPasswordCommand("user-id", "  ")));

        Assert.Equal("NewPassword", exception.ParamName);
    }
}
