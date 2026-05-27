using Api.Tests.TestHelpers;
using Application.UseCases.ValidateToken;
using Grpc.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Protos.Auth;
using ValidateTokenUseCaseResponse = Application.UseCases.ValidateToken.ValidateTokenResponse;

namespace Api.Tests;

public class ValidateTokenGrpcTests
{
    [Fact]
    public async Task ShouldReturnValidationResult()
    {
        var useCase = Substitute.For<IValidateTokenUseCase>();
        useCase.ExecuteAsync(Arg.Any<ValidateTokenCommand>())
            .Returns(new ValidateTokenUseCaseResponse(true, "userId", null));

        var service = AuthGrpcServiceTestFactory.Create(validateTokenUseCase: useCase);

        var response = await service.ValidateToken(
            new ValidateTokenRequest { AccessToken = "access-token" },
            Substitute.For<ServerCallContext>());

        Assert.True(response.IsValid);
        Assert.Equal("userId", response.UserId);

        await useCase.Received(1).ExecuteAsync(Arg.Is<ValidateTokenCommand>(c => c.AccessToken == "access-token"));
    }

    [Fact]
    public async Task ShouldMapNullUserId_ToEmptyString()
    {
        var useCase = Substitute.For<IValidateTokenUseCase>();
        useCase.ExecuteAsync(Arg.Any<ValidateTokenCommand>())
            .Returns(new ValidateTokenUseCaseResponse(false, null, "Invalid or expired token"));

        var service = AuthGrpcServiceTestFactory.Create(validateTokenUseCase: useCase);

        var response = await service.ValidateToken(new ValidateTokenRequest(), Substitute.For<ServerCallContext>());

        Assert.False(response.IsValid);
        Assert.Equal(string.Empty, response.UserId);
        Assert.Equal("Invalid or expired token", response.Message);
    }

    [Fact]
    public async Task ShouldThrowInternal_OnUnexpectedException()
    {
        var useCase = Substitute.For<IValidateTokenUseCase>();
        useCase.ExecuteAsync(Arg.Any<ValidateTokenCommand>()).Throws(new Exception("boom"));

        var service = AuthGrpcServiceTestFactory.Create(validateTokenUseCase: useCase);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.ValidateToken(new ValidateTokenRequest(), Substitute.For<ServerCallContext>()));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.Equal("Token validation failed", ex.Status.Detail);
    }
}
