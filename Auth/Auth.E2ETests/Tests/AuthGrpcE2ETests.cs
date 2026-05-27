using Auth.E2ETests.Infrastructure;
using FluentAssertions;
using Grpc.Core;
using Infrastructure.DbContext;
using Microsoft.EntityFrameworkCore;
using Protos.Auth;
using Xunit;

namespace Auth.E2ETests.Tests;

[Collection("E2E")]
public sealed class AuthGrpcE2ETests : IClassFixture<E2ETestServer>, IAsyncLifetime
{
    private readonly E2ETestServer _server;
    private AuthService.AuthServiceClient _client = null!;

    public AuthGrpcE2ETests(E2ETestServer server)
    {
        _server = server;
    }

    public Task InitializeAsync()
    {
        _client = _server.CreateAuthClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_Login_ValidateToken_ShouldWorkEndToEnd()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";

        var register = await _client.RegisterAsync(new RegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "John Doe",
            Phone = "+4911111111"
        });

        register.UserId.Should().NotBeNullOrWhiteSpace();
        register.Email.Should().Be(email);

        var verifyCode = await GetLatestEmailVerificationCodeAsync(register.UserId);
        await _client.VerifyEmailAsync(new VerifyEmailRequest { Token = verifyCode });

        var login = await _client.LoginAsync(new LoginRequest
        {
            Email = email,
            Password = password
        });

        login.AccessToken.Should().NotBeNullOrWhiteSpace();
        login.RefreshToken.Should().NotBeNullOrWhiteSpace();
        login.TokenType.Should().Be("Bearer");

        var validate = await _client.ValidateTokenAsync(new ValidateTokenRequest
        {
            AccessToken = login.AccessToken
        });

        validate.IsValid.Should().BeTrue();
        validate.UserId.Should().Be(register.UserId);
    }

    [Fact]
    public async Task RefreshToken_ThenRevoke_ShouldBlockFurtherRefresh()
    {
        var (refreshUserId, email, password) = await RegisterUserAsync();

        var verifyCode = await GetLatestEmailVerificationCodeAsync(refreshUserId);
        await _client.VerifyEmailAsync(new VerifyEmailRequest { Token = verifyCode });

        var login = await _client.LoginAsync(new LoginRequest
        {
            Email = email,
            Password = password
        });

        var refreshed = await _client.RefreshTokenAsync(new RefreshTokenRequest
        {
            RefreshToken = login.RefreshToken
        });

        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();

        var revoked = await _client.RevokeTokenAsync(new RevokeTokenRequest
        {
            RefreshToken = login.RefreshToken
        });

        revoked.Success.Should().BeFalse();
        revoked.Message.Should().Be("Refresh token already revoked");

        Func<Task> refreshAgain = async () =>
        {
            await _client.RefreshTokenAsync(new RefreshTokenRequest
            {
                RefreshToken = login.RefreshToken
            });
        };

        var ex = await refreshAgain.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task RequestReset_ThenResetPassword_ShouldAllowLoginWithNewPassword()
    {
        var (userId, email, oldPassword) = await RegisterUserAsync();

        var emailVerifyCode = await GetLatestEmailVerificationCodeAsync(userId);
        await _client.VerifyEmailAsync(new VerifyEmailRequest { Token = emailVerifyCode });

        var requestReset = await _client.RequestPasswordResetAsync(new RequestPasswordResetRequest
        {
            Email = email,
            IpAddress = "127.0.0.1"
        });

        requestReset.Success.Should().BeTrue();

        var resetToken = await GetLatestPasswordResetTokenAsync(userId);

        var reset = await _client.ResetPasswordAsync(new ResetPasswordRequest
        {
            Token = resetToken,
            NewPassword = "NewPassword123!"
        });

        reset.Success.Should().BeTrue();

        Func<Task> loginWithOldPassword = async () =>
        {
            await _client.LoginAsync(new LoginRequest
            {
                Email = email,
                Password = oldPassword
            });
        };

        var oldPasswordEx = await loginWithOldPassword.Should().ThrowAsync<RpcException>();
        oldPasswordEx.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

        var loginNew = await _client.LoginAsync(new LoginRequest
        {
            Email = email,
            Password = "NewPassword123!"
        });

        loginNew.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task VerifyEmail_ShouldSucceedThenRejectReusedCode()
    {
        var (userId, _, _) = await RegisterUserAsync();
        var verifyCode = await GetLatestEmailVerificationCodeAsync(userId);

        var first = await _client.VerifyEmailAsync(new VerifyEmailRequest
        {
            Token = verifyCode
        });

        first.Success.Should().BeTrue();
        first.UserId.Should().Be(userId);

        var second = await _client.VerifyEmailAsync(new VerifyEmailRequest
        {
            Token = verifyCode
        });

        second.Success.Should().BeFalse();
        second.Message.Should().Be("Verification token has already been used");
    }

    private async Task<(string UserId, string Email, string Password)> RegisterUserAsync()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var password = "Password123!";

        var response = await _client.RegisterAsync(new RegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "John Doe",
            Phone = "+4911111111"
        });

        return (response.UserId, email, password);
    }

    private async Task<string> GetLatestPasswordResetTokenAsync(string userId)
    {
        await using var scope = _server.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var token = await db.PasswordResetTokens
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Token)
            .FirstAsync();

        return token;
    }

    private async Task<string> GetLatestEmailVerificationCodeAsync(string userId)
    {
        await using var scope = _server.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var code = await db.EmailVerificationTokens
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Code)
            .FirstAsync();

        return code;
    }
}
