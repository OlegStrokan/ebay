using Api.Tests.TestHelpers;
using Application.UseCases.VerifyEmail;
using Grpc.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Protos.Auth;
using VerifyEmailUseCaseResponse = Application.UseCases.VerifyEmail.VerifyEmailResponse;

namespace Api.Tests;

public class VerifyEmailGrpcTests
{
    [Fact]
    public async Task ShouldReturnVerifyEmailResult()
    {
        var useCase = Substitute.For<IVerifyEmailUseCase>();
        useCase.ExecuteAsync(Arg.Any<VerifyEmailCommand>())
            .Returns(new VerifyEmailUseCaseResponse(true, "Email verified successfully", "userId"));

        var service = AuthGrpcServiceTestFactory.Create(verifyEmailUseCase: useCase);

        var response = await service.VerifyEmail(
            new VerifyEmailRequest { Token = "verify-token" },
            Substitute.For<ServerCallContext>());

        Assert.True(response.Success);
        Assert.Equal("Email verified successfully", response.Message);
        Assert.Equal("userId", response.UserId);

        await useCase.Received(1).ExecuteAsync(Arg.Is<VerifyEmailCommand>(c => c.Token == "verify-token"));
    }

    [Fact]
    public async Task ShouldMapNullUserId_ToEmptyString()
    {
        var useCase = Substitute.For<IVerifyEmailUseCase>();
        useCase.ExecuteAsync(Arg.Any<VerifyEmailCommand>())
            .Returns(new VerifyEmailUseCaseResponse(false, "Invalid verification code", null));

        var service = AuthGrpcServiceTestFactory.Create(verifyEmailUseCase: useCase);

        var response = await service.VerifyEmail(new VerifyEmailRequest(), Substitute.For<ServerCallContext>());

        Assert.False(response.Success);
        Assert.Equal("Invalid verification code", response.Message);
        Assert.Equal(string.Empty, response.UserId);
    }

    [Fact]
    public async Task ShouldThrowInternal_OnUnexpectedException()
    {
        var useCase = Substitute.For<IVerifyEmailUseCase>();
        useCase.ExecuteAsync(Arg.Any<VerifyEmailCommand>()).Throws(new Exception("boom"));

        var service = AuthGrpcServiceTestFactory.Create(verifyEmailUseCase: useCase);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.VerifyEmail(new VerifyEmailRequest(), Substitute.For<ServerCallContext>()));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.Equal("Email verification failed", ex.Status.Detail);
    }
}
