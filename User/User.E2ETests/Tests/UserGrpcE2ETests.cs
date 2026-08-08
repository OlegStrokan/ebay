using FluentAssertions;
using Grpc.Core;
using Protos.Role;
using Protos.User;
using User.E2ETests.Infrastructure;
using Xunit;

namespace User.E2ETests.Tests;

[Collection("E2E")]
public sealed class UserGrpcE2ETests : IClassFixture<E2ETestServer>, IAsyncLifetime
{
    private readonly E2ETestServer _server;
    private UserServiceProto.UserServiceProtoClient _client = null!;

    public UserGrpcE2ETests(E2ETestServer server)
    {
        _server = server;
    }

    public Task InitializeAsync()
    {
        _client = _server.CreateUserClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateUser_ThenGetUserById_ShouldReturnNormalizedData()
    {
        var rawEmail = $"  USER-{Guid.NewGuid():N}@Example.COM  ";

        var created = await _client.CreateUserAsync(new CreateUserRequest
        {
            FullName = "  John Doe  ",
            Email = rawEmail,
            Password = "password123",
            Phone = "  +420123456  ",
            CountryCode = " de ",
            CustomerTier = CustomerTierProto.Premium,
        });

        created.Data.Should().NotBeNull();
        created.Data.Id.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(created.Data.Id, out _).Should().BeTrue();
        created.Data.Email.Should().Be(rawEmail.Trim().ToLowerInvariant());
        created.Data.FullName.Should().Be("John Doe");
        created.Data.Phone.Should().Be("+420123456");
        created.Data.CountryCode.Should().Be("DE");
        created.Data.CustomerTier.Should().Be(CustomerTierProto.Premium);
        created.Data.Status.Should().Be(UserStatusProto.Active);
        created.Data.IsEmailVerified.Should().BeFalse();

        var fetched = await _client.GetUserByIdAsync(new GetUserByIdRequest
        {
            Id = created.Data.Id
        });

        fetched.Data.Should().NotBeNull();
        fetched.Data.Id.Should().Be(created.Data.Id);
        fetched.Data.Email.Should().Be(rawEmail.Trim().ToLowerInvariant());
        fetched.Data.CountryCode.Should().Be("DE");
        fetched.Data.CustomerTier.Should().Be(CustomerTierProto.Premium);
        fetched.Data.Status.Should().Be(UserStatusProto.Active);
        fetched.Data.IsEmailVerified.Should().BeFalse();
    }

    [Fact]
    public async Task GetUserByEmail_ShouldReturnUserWithoutPasswordHash()
    {
        var email = $"lookup-{Guid.NewGuid():N}@example.com";
        await CreateUserAsync(email: email, password: "Password123");

        var byEmail = await _client.GetUserByEmailAsync(new GetUserByEmailRequest
        {
            Email = $"  {email.ToUpperInvariant()}  "
        });

        byEmail.Data.Should().NotBeNull();
        byEmail.Data.Email.Should().Be(email);
        byEmail.Data.IsEmailVerified.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyCredentials_ShouldReturnUserOnlyWhenPasswordMatches()
    {
        var email = $"auth-{Guid.NewGuid():N}@example.com";
        await CreateUserAsync(email: email, password: "Password123");

        var valid = await _client.VerifyCredentialsAsync(new VerifyCredentialsRequest
        {
            Email = $"  {email.ToUpperInvariant()}  ",
            Password = "Password123"
        });

        valid.IsValid.Should().BeTrue();
        valid.Data.Should().NotBeNull();
        valid.Data.Email.Should().Be(email);

        var invalid = await _client.VerifyCredentialsAsync(new VerifyCredentialsRequest
        {
            Email = email,
            Password = "wrong-password"
        });

        invalid.IsValid.Should().BeFalse();
        invalid.Data.Should().BeNull();
    }

    [Fact]
    public async Task VerifyUserEmail_ShouldSetFlag_AndBeIdempotent()
    {
        var created = await CreateUserAsync();

        var first = await _client.VerifyUserEmailAsync(new VerifyUserEmailRequest
        {
            UserId = created.Id
        });
        first.Success.Should().BeTrue();

        var second = await _client.VerifyUserEmailAsync(new VerifyUserEmailRequest
        {
            UserId = created.Id
        });
        second.Success.Should().BeTrue();

        var fetched = await _client.GetUserByIdAsync(new GetUserByIdRequest { Id = created.Id });
        fetched.Data.IsEmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyUserEmail_ShouldReturnFalse_WhenUserMissing()
    {
        var response = await _client.VerifyUserEmailAsync(new VerifyUserEmailRequest
        {
            UserId = Guid.NewGuid().ToString()
        });

        response.Success.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateUserPassword_ShouldReplacePasswordHashForAuthFlow()
    {
        var email = $"reset-{Guid.NewGuid():N}@example.com";
        await CreateUserAsync(email: email, password: "Password123");

        var before = await _client.VerifyCredentialsAsync(new VerifyCredentialsRequest
        {
            Email = email,
            Password = "Password123"
        });
        before.IsValid.Should().BeTrue();

        const string newPassword = "NewPassword123!";
        var update = await _client.UpdateUserPasswordAsync(new UpdateUserPasswordRequest
        {
            UserId = before.Data!.Id,
            NewPassword = newPassword
        });

        update.Success.Should().BeTrue();
        update.Message.Should().Be("Password updated successfully");

        var oldPasswordAttempt = await _client.VerifyCredentialsAsync(new VerifyCredentialsRequest
        {
            Email = email,
            Password = "Password123"
        });
        oldPasswordAttempt.IsValid.Should().BeFalse();

        var newPasswordAttempt = await _client.VerifyCredentialsAsync(new VerifyCredentialsRequest
        {
            Email = email,
            Password = newPassword
        });
        newPasswordAttempt.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateUserPassword_ShouldReturnFailure_WhenUserMissing()
    {
        var response = await _client.UpdateUserPasswordAsync(new UpdateUserPasswordRequest
        {
            UserId = Guid.NewGuid().ToString(),
            NewPassword = "AnyPassword123!"
        });

        response.Success.Should().BeFalse();
        response.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task CreateUser_WithDuplicateEmail_ShouldReturnAlreadyExists()
    {
        var email = $"dupe-{Guid.NewGuid():N}@example.com";

        await _client.CreateUserAsync(new CreateUserRequest
        {
            FullName = "User One",
            Email = email,
            Password = "password123",
            Phone = "+111111",
            CountryCode = "DE",
            CustomerTier = CustomerTierProto.Standard,
        });

        Func<Task> act = async () =>
        {
            await _client.CreateUserAsync(new CreateUserRequest
            {
                FullName = "User Two",
                Email = $"  {email.ToUpperInvariant()}  ",
                Password = "password123",
                Phone = "+222222",
                CountryCode = "DE",
                CustomerTier = CustomerTierProto.Standard,
            });
        };

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task UpdateUser_ShouldPersistNormalizedFields()
    {
        var created = await CreateUserAsync();

        var updated = await _client.UpdateUserAsync(new UpdateUserRequest
        {
            Id = created.Id,
            FullName = "  Updated User  ",
            Email = "  UPDATED-EMAIL@EXAMPLE.COM  ",
            Phone = "  +333333  ",
            CountryCode = " us ",
            CustomerTier = CustomerTierProto.Subscriber,
        });

        updated.Data.Should().NotBeNull();
        updated.Data.Id.Should().Be(created.Id);
        updated.Data.FullName.Should().Be("Updated User");
        updated.Data.Email.Should().Be("updated-email@example.com");
        updated.Data.Phone.Should().Be("+333333");
        updated.Data.CountryCode.Should().Be("US");
        updated.Data.CustomerTier.Should().Be(CustomerTierProto.Subscriber);

        var fetched = await _client.GetUserByIdAsync(new GetUserByIdRequest { Id = created.Id });
        fetched.Data.FullName.Should().Be("Updated User");
        fetched.Data.Email.Should().Be("updated-email@example.com");
        fetched.Data.CountryCode.Should().Be("US");
        fetched.Data.CustomerTier.Should().Be(CustomerTierProto.Subscriber);
    }

    [Fact]
    public async Task RestrictUser_ShouldSetStatusToRestricted()
    {
        var target = await CreateUserAsync();
        var actor  = await CreateUserAsync();

        // Grant the actor Admin role so they have permission to restrict. AssignedBy must be
        // SYSTEM: AssignRoleUseCase blocks a user promoting themselves above their own tier.
        await _client.AssignRoleAsync(new AssignRoleRequest
        {
            UserId     = actor.Id,
            RoleName   = "Admin",
            AssignedBy = "SYSTEM",
        });

        var restricted = await _client.RestrictUserAsync(new RestrictUserRequest
        {
            TargetUserId = target.Id,
            ActorUserId  = actor.Id,
            Type         = RestrictionTypeProto.RestrictionTypeRestricted,
            Reason       = "E2E test restriction",
        });

        restricted.Data.Should().NotBeNull();
        restricted.Data.Id.Should().Be(target.Id);
        restricted.Data.Status.Should().Be(UserStatusProto.Restricted);

        var fetched = await _client.GetUserByIdAsync(new GetUserByIdRequest { Id = target.Id });
        fetched.Data.Status.Should().Be(UserStatusProto.Restricted);
    }

    [Fact]
    public async Task LiftRestriction_ShouldRestoreStatusToActive()
    {
        var target = await CreateUserAsync();
        var actor  = await CreateUserAsync();

        await _client.AssignRoleAsync(new AssignRoleRequest
        {
            UserId     = actor.Id,
            RoleName   = "Admin",
            AssignedBy = "SYSTEM",
        });

        await _client.RestrictUserAsync(new RestrictUserRequest
        {
            TargetUserId = target.Id,
            ActorUserId  = actor.Id,
            Type         = RestrictionTypeProto.RestrictionTypeRestricted,
            Reason       = "Temporary restriction",
        });

        var lifted = await _client.LiftRestrictionAsync(new LiftRestrictionRequest
        {
            TargetUserId = target.Id,
            ActorUserId  = actor.Id,
        });

        lifted.Success.Should().BeTrue();

        var fetched = await _client.GetUserByIdAsync(new GetUserByIdRequest { Id = target.Id });
        fetched.Data.Status.Should().Be(UserStatusProto.Active);
    }

    [Fact]
    public async Task UpdatePassword_ShouldValidateCurrentPassword_AndApplyChanges()
    {
        var created = await CreateUserAsync(password: "oldPassword123");

        Func<Task> wrongPassword = async () =>
        {
            await _client.UpdatePasswordAsync(new UpdatePasswordRequest
            {
                Id = created.Id,
                CurrentPassword = "wrongPassword",
                NewPassword = "newPassword123",
            });
        };

        var wrongPasswordEx = await wrongPassword.Should().ThrowAsync<RpcException>();
        wrongPasswordEx.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);

        await _client.UpdatePasswordAsync(new UpdatePasswordRequest
        {
            Id = created.Id,
            CurrentPassword = "oldPassword123",
            NewPassword = "newPassword123",
        });

        Func<Task> oldPasswordAfterUpdate = async () =>
        {
            await _client.UpdatePasswordAsync(new UpdatePasswordRequest
            {
                Id = created.Id,
                CurrentPassword = "oldPassword123",
                NewPassword = "anotherPassword123",
            });
        };

        var oldPasswordEx = await oldPasswordAfterUpdate.Should().ThrowAsync<RpcException>();
        oldPasswordEx.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);

        await _client.UpdatePasswordAsync(new UpdatePasswordRequest
        {
            Id = created.Id,
            CurrentPassword = "newPassword123",
            NewPassword = "anotherPassword123",
        });
    }

    [Fact]
    public async Task DeleteUser_ShouldBeIdempotent_AndGetShouldReturnNotFound()
    {
        var created = await CreateUserAsync();

        await _client.DeleteUserAsync(new DeleteUserRequest { Id = created.Id });

        Func<Task> getDeleted = async () =>
        {
            await _client.GetUserByIdAsync(new GetUserByIdRequest { Id = created.Id });
        };

        var getDeletedEx = await getDeleted.Should().ThrowAsync<RpcException>();
        getDeletedEx.Which.StatusCode.Should().Be(StatusCode.NotFound);

        Func<Task> deleteAgain = async () =>
        {
            await _client.DeleteUserAsync(new DeleteUserRequest { Id = created.Id });
        };

        await deleteAgain.Should().NotThrowAsync();
    }

    private async Task<UserProto> CreateUserAsync(
        string? email = null,
        string password = "password123")
    {
        var request = new CreateUserRequest
        {
            FullName = "John Doe",
            Email = email ?? $"user-{Guid.NewGuid():N}@example.com",
            Password = password,
            Phone = "+420123456",
            CountryCode = "DE",
            CustomerTier = CustomerTierProto.Standard,
        };

        var response = await _client.CreateUserAsync(request);
        return response.Data;
    }
}
