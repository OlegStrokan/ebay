using Api.GrpcServices;
using Application.UseCases.Login;
using Application.UseCases.RefreshToken;
using Application.UseCases.Register;
using Application.UseCases.RequestPasswordReset;
using Application.UseCases.ResetPassword;
using Application.UseCases.RevokeToken;
using Application.UseCases.ResendVerificationEmail;
using Application.UseCases.ValidateToken;
using Application.UseCases.VerifyEmail;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Api.Tests.TestHelpers;

internal static class AuthGrpcServiceTestFactory
{
    public static AuthGrpcService Create(
        IRegisterUseCase? registerUseCase = null,
        ILoginUseCase? loginUseCase = null,
        IRefreshTokenUseCase? refreshTokenUseCase = null,
        IRequestPasswordResetUseCase? requestPasswordResetUseCase = null,
        IResetPasswordUseCase? resetPasswordUseCase = null,
        IValidateTokenUseCase? validateTokenUseCase = null,
        IVerifyEmailUseCase? verifyEmailUseCase = null,
        IResendVerificationEmailUseCase? resendVerificationEmailUseCase = null,
        IRevokeTokenUseCase? revokeTokenUseCase = null)
    {
        return new AuthGrpcService(
            Substitute.For<ILogger<AuthGrpcService>>(),
            registerUseCase ?? Substitute.For<IRegisterUseCase>(),
            loginUseCase ?? Substitute.For<ILoginUseCase>(),
            refreshTokenUseCase ?? Substitute.For<IRefreshTokenUseCase>(),
            requestPasswordResetUseCase ?? Substitute.For<IRequestPasswordResetUseCase>(),
            resetPasswordUseCase ?? Substitute.For<IResetPasswordUseCase>(),
            validateTokenUseCase ?? Substitute.For<IValidateTokenUseCase>(),
            verifyEmailUseCase ?? Substitute.For<IVerifyEmailUseCase>(),
            resendVerificationEmailUseCase ?? Substitute.For<IResendVerificationEmailUseCase>(),
            revokeTokenUseCase ?? Substitute.For<IRevokeTokenUseCase>());
    }
}
