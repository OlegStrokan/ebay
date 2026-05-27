
using Api.GrpcServices;
using Api.Tests.TestHelpers;
using Application.UseCases.Register;
using Domain.Common;
using Grpc.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Protos.Auth;
using RegisterResponse = Application.UseCases.Register.RegisterResponse;

namespace Api.Tests;

public class RegisterGrpcTests
{
    [Fact]
    public async Task ShouldReturnSuccessResponse()
    {
        var useCase = Substitute.For<IRegisterUseCase>();

        var useCaseResponse = new RegisterResponse(
            "userId",
            "oleh@email.com",
            "Sam Faggotman",
            "Success"
        );

        useCase.ExecuteAsync(Arg.Any<RegisterCommand>()).Returns(useCaseResponse);

        var service = AuthGrpcServiceTestFactory.Create(registerUseCase: useCase);

        var request = new RegisterRequest
        {
            Email = "userId",
            Password = "password",
            FullName = "Sam Faggotman",
            Phone = "+3029320932"
        };

        var response = await service.Register(request, Substitute.For<ServerCallContext>());

        Assert.Equal("userId", response.UserId);
        Assert.Equal("Success", response.Message);


        await useCase.Received(1).ExecuteAsync(Arg.Is<RegisterCommand>(c =>
            c.Email == request.Email &&
            c.Fullname == request.FullName));
    }

    [Fact]
    public async Task ShouldThrowWhenDomainExceptionOccurs()
    {
        var useCase = Substitute.For<IRegisterUseCase>();

        useCase.ExecuteAsync(Arg.Any<RegisterCommand>()).Throws(new DomainException("Email already exists"));
        
        var service = AuthGrpcServiceTestFactory.Create(registerUseCase: useCase);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            service.Register(new RegisterRequest(), Substitute.For<ServerCallContext>()));


        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("Email already exists", exception.Status.Detail);
    }
    
    [Fact]
    public async Task ShouldThrowWhenUnexpectedExceptionOccurs()
    {
        var useCase = Substitute.For<IRegisterUseCase>();

        useCase.ExecuteAsync(Arg.Any<RegisterCommand>()).Throws(new Exception("Unexpected shit"));
        
        var service = AuthGrpcServiceTestFactory.Create(registerUseCase: useCase);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            service.Register(new RegisterRequest(), Substitute.For<ServerCallContext>()));


        Assert.Equal(StatusCode.Internal, exception.StatusCode);
        Assert.Contains("Registration failed", exception.Status.Detail);
        
    }
}