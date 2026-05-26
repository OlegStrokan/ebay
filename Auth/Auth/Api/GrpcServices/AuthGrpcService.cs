using Application.UseCases.Login;
using Application.UseCases.RefreshToken;
using Application.UseCases.Register;
using Application.UseCases.RequestPasswordReset;
using Application.UseCases.ResetPassword;
using Application.UseCases.RevokeToken;
using Application.UseCases.ValidateToken;
using Application.UseCases.VerifyEmail;
using Domain.Common;
using Grpc.Core;
using Protos.Auth;
using LoginResponseProto = Protos.Auth.LoginResponse;
using RefreshTokenResponseProto = Protos.Auth.RefreshTokenResponse;
using RegisterResponseProto = Protos.Auth.RegisterResponse;
using RequestPasswordResetResponseProto = Protos.Auth.RequestPasswordResetResponse;
using ResetPasswordResponseProto = Protos.Auth.ResetPasswordResponse;
using RevokeTokenResponseProto = Protos.Auth.RevokeTokenResponse;
using ValidateTokenResponseProto = Protos.Auth.ValidateTokenResponse;
using VerifyEmailResponseProto = Protos.Auth.VerifyEmailResponse;

namespace Api.GrpcServices;

public class AuthGrpcService(
    ILogger<AuthGrpcService> logger,
    IRegisterUseCase registerUseCase,
    ILoginUseCase loginUseCase,
    IRefreshTokenUseCase refreshTokenUseCase,
    IRequestPasswordResetUseCase requestPasswordResetUseCase,
    IResetPasswordUseCase resetPasswordUseCase,
    IValidateTokenUseCase validateTokenUseCase,
    IVerifyEmailUseCase verifyEmailUseCase,
    IRevokeTokenUseCase revokeTokenUseCase) : AuthService.AuthServiceBase
{
    public override async Task<RegisterResponseProto> Register(RegisterRequest request, ServerCallContext context)
    {
        try
        {
            logger.LogInformation("Register request received for email: {Email}", request.Email);

            var command = new RegisterCommand(request.Email, request.Password, request.FullName, request.Phone);
            var response = await registerUseCase.ExecuteAsync(command);

            return new RegisterResponseProto
            {
                UserId = response.UserId,
                Email = response.Email,
                FullName = response.Fullname,
                Message = response.Message,
            };
        }
        catch (DomainException ex)
        {
            logger.LogWarning(ex, "Registration failed: {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during registration");
            throw new RpcException(new Status(StatusCode.Internal, "Registration failed"));
        }
    }

    public override async Task<LoginResponseProto> Login(LoginRequest request, ServerCallContext context)
    {
        try
        {
            logger.LogInformation("Login request received for email: {Email}", request.Email);

            var command = new LoginCommand(request.Email, request.Password);
            var response = await loginUseCase.ExecuteAsync(command);

            return new LoginResponseProto
            {
                AccessToken = response.AccessToken,
                ExpiresIn = response.ExpiresIn,
                RefreshToken = response.RefreshToken,
                TokenType = response.TokenType,
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Login failed for email: {Email}", request.Email);
            throw new RpcException(new Status(StatusCode.Unauthenticated, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during login");
            throw new RpcException(new Status(StatusCode.Internal, "Login failed"));
        }
    }

    public override async Task<RefreshTokenResponseProto> RefreshToken(RefreshTokenRequest request, ServerCallContext context)
    {
        try
        {
            logger.LogInformation("RefreshToken request received");

            var command = new RefreshTokenCommand(request.RefreshToken);
            var response = await refreshTokenUseCase.ExecuteAsync(command);

            return new RefreshTokenResponseProto
            {
                AccessToken = response.AccessToken,
                ExpiresIn = response.ExpiresIn,
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Token refresh failed: {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.Unauthenticated, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error refreshing token");
            throw new RpcException(new Status(StatusCode.Internal, "Token refresh failed"));
        }
    }

    public override async Task<RevokeTokenResponseProto> RevokeToken(RevokeTokenRequest request, ServerCallContext context)
    {
        try
        {
            logger.LogInformation("RevokeToken request received");

            var command = new RevokeTokenCommand(request.RefreshToken);
            var response = await revokeTokenUseCase.ExecuteAsync(command);

            return new RevokeTokenResponseProto
            {
                Success = response.Success,
                Message = response.Message,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error revoking token");
            throw new RpcException(new Status(StatusCode.Internal, "Token revocation failed"));
        }
    }

    public override async Task<ValidateTokenResponseProto> ValidateToken(ValidateTokenRequest request, ServerCallContext context)
    {
        try
        {
            var command = new ValidateTokenCommand(request.AccessToken);
            var response = await validateTokenUseCase.ExecuteAsync(command);

            return new ValidateTokenResponseProto
            {
                IsValid = response.IsValid,
                UserId = response.UserId ?? string.Empty,
                Message = response.Message ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating token");
            throw new RpcException(new Status(StatusCode.Internal, "Token validation failed"));
        }
    }

    public override async Task<VerifyEmailResponseProto> VerifyEmail(VerifyEmailRequest request, ServerCallContext context)
    {
        try
        {
            logger.LogInformation("VerifyEmail request received");

            var command = new VerifyEmailCommand(request.Code);
            var response = await verifyEmailUseCase.ExecuteAsync(command);

            return new VerifyEmailResponseProto
            {
                Success = response.Success,
                UserId = response.UserId ?? string.Empty,
                Message = response.Message,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error verifying email");
            throw new RpcException(new Status(StatusCode.Internal, "Email verification failed"));
        }
    }

    public override async Task<RequestPasswordResetResponseProto> RequestPasswordReset(RequestPasswordResetRequest request, ServerCallContext context)
    {
        try
        {
            logger.LogInformation("RequestPasswordReset request received for email: {Email}", request.Email);

            var command = new RequestPasswordResetCommand(request.Email, request.IpAddress);
            var response = await requestPasswordResetUseCase.ExecuteAsync(command);

            return new RequestPasswordResetResponseProto
            {
                Success = response.Success,
                Message = response.Message,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error requesting password reset");
            throw new RpcException(new Status(StatusCode.Internal, "Password reset request failed"));
        }
    }

    public override async Task<ResetPasswordResponseProto> ResetPassword(ResetPasswordRequest request, ServerCallContext context)
    {
        try
        {
            logger.LogInformation("Reset password request received");

            var command = new ResetPasswordCommand(request.Token, request.NewPassword);
            var response = await resetPasswordUseCase.ExecuteAsync(command);

            return new ResetPasswordResponseProto
            {
                Success = response.Success,
                Message = response.Message,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resetting password");
            throw new RpcException(new Status(StatusCode.Internal, "Password reset request failed"));
        }
    }
}
