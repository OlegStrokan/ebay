using Application.UseCases.AssignRole;
using Domain.Entities.Role;
using Domain.Entities.User;
using Domain.Repositories;
using NSubstitute;

namespace Application.Tests;

public class AssignRoleUseCaseTests
{
    private static UserEntity MakeUser(string id, params string[] existingRoleNames)
    {
        var userRoles = existingRoleNames.Select(name =>
        {
            var role = new RoleEntity { Id = $"role-{name.ToLower()}", Name = name, Description = name, IsSystem = true };
            return new UserRoleEntity { UserId = id, RoleId = role.Id, AssignedBy = "system", AssignedAt = DateTime.UtcNow, Role = role };
        }).ToList();

        return new UserEntity
        {
            Id = id,
            Email = $"{id}@example.com",
            Password = "hashed",
            Fullname = "Test User",
            Phone = "+1234567890",
            CountryCode = "DE",
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserRoles = userRoles,
        };
    }

    [Fact]
    public async Task ShouldAssignRole_WhenValid()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var roleRepository = Substitute.For<IRoleRepository>();

        var user = MakeUser("user-id"); // no roles yet
        var caller = MakeUser("admin-id", "Admin");
        var role = new RoleEntity { Id = "role-moderator", Name = "Moderator", Description = "Moderator", IsSystem = true };
        var command = new AssignRoleCommand("user-id", "Moderator", "admin-id");

        userRepository.GetUserById("user-id").Returns(user);
        userRepository.GetUserById("admin-id").Returns(caller);
        roleRepository.GetByNameAsync("Moderator").Returns(role);

        var useCase = new AssignRoleUseCase(userRepository, roleRepository);
        var result = await useCase.ExecuteAsync(command);

        Assert.True(result.Success);

        await roleRepository.Received(1).AssignRoleAsync(
            Arg.Is<UserRoleEntity>(ur =>
                ur.UserId == "user-id" &&
                ur.RoleId == "role-moderator" &&
                ur.AssignedBy == "admin-id"));
    }

    [Fact]
    public async Task ShouldThrow_WhenUserNotFound()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var roleRepository = Substitute.For<IRoleRepository>();
        var command = new AssignRoleCommand("missing-id", "Admin", "admin-id");

        userRepository.GetUserById("missing-id").Returns((UserEntity?)null);

        var useCase = new AssignRoleUseCase(userRepository, roleRepository);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => useCase.ExecuteAsync(command));

        await roleRepository.DidNotReceive().AssignRoleAsync(Arg.Any<UserRoleEntity>());
    }

    [Fact]
    public async Task ShouldThrow_WhenRoleDoesNotExist()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var roleRepository = Substitute.For<IRoleRepository>();

        var user = MakeUser("user-id");
        var command = new AssignRoleCommand("user-id", "FakeRole", "admin-id");

        userRepository.GetUserById("user-id").Returns(user);
        roleRepository.GetByNameAsync("FakeRole").Returns((RoleEntity?)null);

        var useCase = new AssignRoleUseCase(userRepository, roleRepository);

        await Assert.ThrowsAsync<ArgumentException>(
            () => useCase.ExecuteAsync(command));
    }

    [Fact]
    public async Task ShouldThrow_WhenUserAlreadyHasRole()
    {
        var userRepository = Substitute.For<IUserRepository>();
        var roleRepository = Substitute.For<IRoleRepository>();

        var user = MakeUser("user-id", "Admin"); // already has Admin
        var role = new RoleEntity { Id = "role-admin", Name = "Admin", Description = "Admin", IsSystem = true };
        var command = new AssignRoleCommand("user-id", "Admin", "super-admin-id");

        userRepository.GetUserById("user-id").Returns(user);
        roleRepository.GetByNameAsync("Admin").Returns(role);

        var useCase = new AssignRoleUseCase(userRepository, roleRepository);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(command));

        await roleRepository.DidNotReceive().AssignRoleAsync(Arg.Any<UserRoleEntity>());
    }
}
