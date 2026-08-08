using Grpc.Core;
using Infrastructure.Gateways;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Protos.User;


namespace Infrastructure.Tests;

public class UserGatewayTests
{
    // Substituted IConfiguration returns null for InternalServices:ApiKey, so the gateway
    // sends an empty x-internal-api-key — which is what these tests assume.
    private static UserGateway CreateSut(UserServiceProto.UserServiceProtoClient client) =>
        new(client, Substitute.For<IConfiguration>(), Substitute.For<ILogger<UserGateway>>());

    [Fact]
    public async Task ShouldReturnsUserId_WhenUserIsCreatedSuccessfully()
    {
        var client = Substitute.For<UserServiceProto.UserServiceProtoClient>();
        var sut = CreateSut(client);

        var email = "test@example.com";
        var expectedId = "userId";

        var response = new CreateUserResponse { Data = new UserProto { Id = expectedId } };

        client.CreateUserAsync(Arg.Any<CreateUserRequest>())
            .Returns(GrpcTestHelper.CreateAsyncUnaryCall(response));


        var result = await sut.CreateUserAsync(email, "password", "John Hitler", "+42020398298");

        Assert.Equal(expectedId, result);
    }

    [Fact]
    public async Task ShouldThrowInvalidOperationException_WhenResponseDataIsNull()
    {
        var client = Substitute.For<UserServiceProto.UserServiceProtoClient>();
        var sut = CreateSut(client);
        
        client.CreateUserAsync(Arg.Any<CreateUserRequest>())
            .Returns(GrpcTestHelper.CreateAsyncUnaryCall(new CreateUserResponse { Data = null }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateUserAsync("test@test.com", "hashedPassword", "Just Hitler", "+3920239200"));
        
        Assert.Contains("User service returned no data", exception.Message);
    }
    
        [Fact]
    public async Task ShouldReturnNullWhenUserNotFoundByEmail()
    {
        var client = Substitute.For<UserServiceProto.UserServiceProtoClient>();
        var sut = CreateSut(client);

        var rpcException = new RpcException(new Status(StatusCode.NotFound, "Not Found"));
        // The gateway sends x-internal-api-key, so it calls the Metadata overload.
        client.GetUserByEmailAsync(
                Arg.Any<GetUserByEmailRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Throws(rpcException);
        
        var result = await sut.GetUserByEmailAsync("missing@test.com");

        Assert.Null(result);
        //await client.Received(1).GetUserByEmailAsync(Arg.Is<GetUserByEmailRequest>(r => r.Email == "missing@test.com"));
    }

    [Fact]
    public async Task ShouldReturnUserWhenCredentialsAreValid()
    {
        var client = Substitute.For<UserServiceProto.UserServiceProtoClient>();
        var sut = CreateSut(client);

        var response = new VerifyCredentialsResponse
        {
            IsValid = true,
            Data = new UserProto
            {
                Id = "userId",
                Email = "found@test.com",
                FullName = "Found User",
                Phone = "+123",
                Status = UserStatusProto.Active,
            }
        };

        client.VerifyCredentialsAsync(Arg.Any<VerifyCredentialsRequest>())
            .Returns(GrpcTestHelper.CreateAsyncUnaryCall(response));

        var result = await sut.VerifyCredentialsAsync("found@test.com", "Password123");

        Assert.NotNull(result);
        Assert.Equal("userId", result!.Id);
        Assert.Equal("Found User", result.FullName);
    }

    [Fact]
    public async Task ShouldMapAndReturnUserDtoWhenUserExistsById()
    {
        var client = Substitute.For<UserServiceProto.UserServiceProtoClient>();
        var sut = CreateSut(client);

        var userId = "userId";
        var response = new GetUserByIdResponse
        {
            Data = new UserProto
            {
                Id = userId,
                Email = "found@test.com",
                FullName = "Found User",
                Status = UserStatusProto.Active
            }
        };

        client.GetUserByIdAsync(Arg.Any<GetUserByIdRequest>())
            .Returns(GrpcTestHelper.CreateAsyncUnaryCall(response));

        var result = await sut.GetUserByIdAsync(userId);
        
        Assert.NotNull(result);
        Assert.Equal(userId, result.Id);
        Assert.Equal("Found User", result.FullName);
       // await client.Received(1).GetUserByIdAsync(Arg.Is<GetUserByIdRequest>(r => r.Id == userId));
    }

    [Fact]
    public async Task ShouldReturnTrueWhenEmailIsVerifiedSuccessfully()
    {
        var client = Substitute.For<UserServiceProto.UserServiceProtoClient>();
        var sut = CreateSut(client);

        client.VerifyUserEmailAsync(Arg.Any<VerifyUserEmailRequest>())
            .Returns(GrpcTestHelper.CreateAsyncUnaryCall(new VerifyUserEmailResponse { Success = true }));

        var result = await sut.VerifyUserEmailAsync("userId");

        Assert.True(result);
       // await client.Received(1).VerifyUserEmailAsync(Arg.Is<VerifyUserEmailRequest>(r => r.UserId == "user-1"));
    }

    [Fact]
    public async Task ShouldThrowInvalidOperationExceptionWhenPasswordUpdateFails()
    {
        var client = Substitute.For<UserServiceProto.UserServiceProtoClient>();
        var sut = CreateSut(client);

        client.UpdateUserPasswordAsync(Arg.Any<UpdateUserPasswordRequest>())
            .Throws(new RpcException(new Status(StatusCode.Internal, "Database error")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            sut.UpdateUserPasswordAsync("userId", "newHashedPassword"));

        Assert.Contains("Failed to update password", exception.Message);
    }
}


// helper to satisfy gprc's asyncUnaryCall return type
public static class GrpcTestHelper
{
    public static AsyncUnaryCall<TResponse> CreateAsyncUnaryCall<TResponse>(TResponse response)
    {
        return new AsyncUnaryCall<TResponse>(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }
}