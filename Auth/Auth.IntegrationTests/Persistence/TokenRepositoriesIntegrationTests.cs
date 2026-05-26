using Auth.IntegrationTests.Infrastructure;
using Domain.Entities;
using Domain.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Auth.IntegrationTests.Persistence;

public sealed class TokenRepositoriesIntegrationTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public TokenRepositoriesIntegrationTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RefreshTokenRepository_ShouldCreateGetAndRevokeToken()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();

        var token = new RefreshTokenEntity
        {
            Id = NewId(),
            UserId = NewId(),
            Token = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false
        };

        await repo.CreateAsync(token);
        var fetched = await repo.GetByTokenAsync(token.Token);

        fetched.Should().NotBeNull();
        fetched!.IsRevoked.Should().BeFalse();

        await repo.RevokeTokenAsync(token.Token, revokedById: NewId(), replacedByToken: Guid.NewGuid().ToString("N"));

        var revoked = await repo.GetByTokenAsync(token.Token);
        revoked.Should().NotBeNull();
        revoked!.IsRevoked.Should().BeTrue();
        revoked.RevokedAt.Should().NotBeNull();
        revoked.RevokedById.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshTokenRepository_RevokeAll_ShouldAffectOnlyTargetUser()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();

        var user1 = NewId();
        var user2 = NewId();

        await repo.CreateAsync(new RefreshTokenEntity
        {
            Id = NewId(),
            UserId = user1,
            Token = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false
        });

        await repo.CreateAsync(new RefreshTokenEntity
        {
            Id = NewId(),
            UserId = user2,
            Token = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false
        });

        await repo.RevokeAllUserTokensAsync(user1, revokedById: NewId());

        var user1Active = await repo.GetActiveTokensByUserIdAsync(user1);
        var user2Active = await repo.GetActiveTokensByUserIdAsync(user2);

        user1Active.Should().BeEmpty();
        user2Active.Should().HaveCount(1);
    }

    [Fact]
    public async Task PasswordResetTokenRepository_ShouldMarkTokenAsUsed()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPasswordResetTokenRepository>();

        var token = new PasswordResetTokenEntity
        {
            Id = NewId(),
            UserId = NewId(),
            Token = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false,
            IpAddress = "127.0.0.1"
        };

        await repo.CreateAsync(token);
        await repo.MarkAsUsedAsync(token.Token);

        var fetched = await repo.GetByTokenAsync(token.Token);
        fetched.Should().NotBeNull();
        fetched!.IsUsed.Should().BeTrue();
        fetched.UsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EmailVerificationTokenRepository_GetByUserId_ShouldReturnTokenForRequestedUser()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IEmailVerificationTokenRepository>();

        var userId = NewId();
        var otherUserId = NewId();

        var tokenForTargetUser = new EmailVerificationTokenEntity
        {
            Id = NewId(),
            UserId = userId,
            Code = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        };

        var tokenForOtherUser = new EmailVerificationTokenEntity
        {
            Id = NewId(),
            UserId = otherUserId,
            Code = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
            CreatedAt = DateTime.UtcNow.AddMinutes(1),
            IsUsed = false
        };

        await repo.CreateAsync(tokenForTargetUser);
        await repo.CreateAsync(tokenForOtherUser);

        var fetched = await repo.GetByUserIdAsync(userId);

        fetched.Should().NotBeNull();
        fetched!.Code.Should().Be(tokenForTargetUser.Code);
    }

    [Fact]
    public async Task EmailVerificationTokenRepository_DeleteByUserId_ShouldRemoveOnlyTargetUserTokens()
    {
        await using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IEmailVerificationTokenRepository>();

        var user1 = NewId();
        var user2 = NewId();

        var user1Token = new EmailVerificationTokenEntity
        {
            Id = NewId(),
            UserId = user1,
            Code = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        };

        var user2Token = new EmailVerificationTokenEntity
        {
            Id = NewId(),
            UserId = user2,
            Code = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(12),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        };

        await repo.CreateAsync(user1Token);
        await repo.CreateAsync(user2Token);

        await repo.DeleteByUserIdAsync(user1);

        var deleted = await repo.GetByCodeAsync(user1Token.Code);
        var remaining = await repo.GetByCodeAsync(user2Token.Code);

        deleted.Should().BeNull();
        remaining.Should().NotBeNull();
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..26];
}
