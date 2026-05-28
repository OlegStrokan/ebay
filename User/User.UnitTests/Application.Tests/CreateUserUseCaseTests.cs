using Application.UseCases.CreateUser;
using Domain.Common.Interfaces;
using Domain.Entities.Role;
using Domain.Entities.User;
using Domain.Repositories;
using NSubstitute;
using Xunit;

namespace Application.Tests;

public class CreateUserUseCaseTests
{
    [Fact]
    public async Task ShouldCreateUserAndReturnResponse()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var roleRepository = Substitute.For<IRoleRepository>();
        var passwordHasher = Substitute.For<IPasswordHasher>();

        const string hashedPassword = "$2a$12$hashed";

        var command = new CreateUserCommand(
            "TestUser@Email.com",
            "password123",
            " Oleh Strokan ",
            "+420123456");

        passwordHasher.HashPassword(command.Password).Returns(hashedPassword);

        var now = DateTime.UtcNow;
        var normalizedEmail = "testuser@email.com";
        UserEntity? createdUser = null;

        var savedUser = new UserEntity
        {
            Id = "generated-by-usecase",
            Email = normalizedEmail,
            Password = hashedPassword,
            Fullname = "Oleh Strokan",
            Phone = command.Phone,
            CountryCode = "DE",
            CustomerTier = CustomerTier.Standard,
            Status = UserStatus.Active,
            IsEmailVerified = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        userRepository.ExistsByEmail(normalizedEmail).Returns(false);
        userRepository
            .CreateUser(Arg.Do<UserEntity>(u => createdUser = u))
            .Returns(callInfo => callInfo.Arg<UserEntity>());

        var defaultRole = new RoleEntity { Id = "role-1", Name = "User", Description = "Default buyer/browser" };
        roleRepository.GetByNameAsync("User").Returns(defaultRole);
        userRepository.GetUserById(Arg.Any<string>()).Returns(callInfo =>
        {
            var user = createdUser!;
            user.UserRoles = [new UserRoleEntity { UserId = user.Id, RoleId = defaultRole.Id, AssignedBy = "SYSTEM", AssignedAt = now, Role = defaultRole }];
            return user;
        });

        var useCase = new CreateUserUseCase(userRepository, roleRepository, passwordHasher);

        var result = await useCase.ExecuteAsync(command);

        Assert.True(Guid.TryParse(result.Id, out _));
        Assert.Equal(savedUser.Email, result.Email);
        Assert.Equal(savedUser.Fullname, result.Fullname);
        Assert.Equal(savedUser.CountryCode, result.CountryCode);
        Assert.Equal(savedUser.CustomerTier, result.CustomerTier);
        Assert.Equal(savedUser.Status, result.Status);
        Assert.Equal(savedUser.IsEmailVerified, result.IsEmailVerified);
        Assert.NotNull(createdUser);
        Assert.True(Guid.TryParse(createdUser!.Id, out var _));
        Assert.Equal(normalizedEmail, createdUser.Email);
        Assert.Equal(hashedPassword, createdUser.Password);
        Assert.Equal("Oleh Strokan", createdUser.Fullname);
        Assert.Equal(command.Phone, createdUser.Phone);
        Assert.Equal("DE", createdUser.CountryCode);
        Assert.Equal(CustomerTier.Standard, createdUser.CustomerTier);
        Assert.False(createdUser.IsEmailVerified);
        Assert.NotNull(result.DeliveryInfos);
        Assert.Empty(result.DeliveryInfos);
        Assert.NotNull(result.Roles);
        Assert.Contains("User", result.Roles);

        await userRepository.Received(1).ExistsByEmail(normalizedEmail);
        passwordHasher.Received(1).HashPassword(command.Password);
        await userRepository.Received(1).CreateUser(Arg.Any<UserEntity>());
        await roleRepository.Received(1).GetByNameAsync("User");
        await roleRepository.Received(1).AssignRoleAsync(Arg.Any<UserRoleEntity>());
    }
}