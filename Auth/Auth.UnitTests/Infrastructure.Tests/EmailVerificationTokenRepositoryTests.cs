using Domain.Entities;
using Infrastructure.DbContext;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests;

public class EmailVerificationTokenRepositoryTests
{
    public AppDbContext GetDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        return new AppDbContext(options);
    }


    [Fact]
    public async Task CreateEmailVerificationTokenAsync_Valid()
    {
        var dbContext = GetDbContext("CreateAsync_Db");
        var emailVerificationTokenRepository = new EmailVerificationTokenRepository(dbContext);

        var token = new EmailVerificationTokenEntity
        {
            Id = "tokenId",
            Code = "123456",
            UserId = "userId",
            IsUsed = false,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };

        var result = await emailVerificationTokenRepository.CreateAsync(token);

        Assert.NotNull(result);
        Assert.Equal(token.Code, result.Code);
        Assert.Equal(token.UserId, result.UserId);
        Assert.Equal(token.IsUsed, result.IsUsed);
        Assert.Equal(token.CreatedAt, result.CreatedAt);
        Assert.Equal(token.ExpiresAt, result.ExpiresAt);
        
        var fetchedToken = await emailVerificationTokenRepository.GetByUserIdAsync(token.UserId);
        
        Assert.NotNull(fetchedToken);
        Assert.Equal(token.Code, fetchedToken.Code); 
    }

    [Fact]
    public async Task GetByTokenAsync_Valid()
    {
        var dbContext = GetDbContext("GetByTokenAsync_Exists_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);
        
        var token = new EmailVerificationTokenEntity
        {
            Id = "tokenId",
            Code = "123456",
            UserId = "userId",
            IsUsed = false,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };

        await repository.CreateAsync(token);
        
        var result = await repository.GetByCodeAsync(token.Code);

        Assert.NotNull(result);
        Assert.Equal(token.Code, result.Code);
        Assert.Equal(token.UserId, result.UserId);
        Assert.Equal(token.Id, result.Id);
        Assert.False(result.IsUsed);
    }

    [Fact]
    public async Task GetByTokenAsync_NotFound()
    {
        var dbContext = GetDbContext("GetByTokenAsync_NotExists_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);

        var result = await repository.GetByCodeAsync("nonExistingTokenValue");
        
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByUserIdAsync_LastValidToken()
    {
        var dbContext = GetDbContext("GetByUserIdAsync_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);

        var userId = "userId";

        var oldToken = new EmailVerificationTokenEntity
        {
            Id = "oldTokenId",
            UserId = "userId",
            IsUsed = false,
            Code = "111111",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = (DateTime.UtcNow.AddHours(-1))
        };


        var newestToken = new EmailVerificationTokenEntity()
        {
            Id = "newTokenId",
            UserId = "userId",
            Code = "222222",
            IsUsed = false,
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow
        };

        await repository.CreateAsync(oldToken);
        await repository.CreateAsync(newestToken);
        
        var result = await repository.GetByUserIdAsync(userId);
        
        Assert.NotNull(result);
        Assert.Equal(newestToken.Code, result.Code);
        Assert.False(result.IsUsed);
    }

    [Fact]
    public async Task GetByUserIdAsync_UsedToken()
    {
        var dbContext = GetDbContext("GetByUserIdAsync_All_Users_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);

        var userId = "userId";

        var usedToken = new EmailVerificationTokenEntity
        {
            Id = "usedTokenId",
            UserId = "userId",
            Code = "333333",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow,
            IsUsed = true,
            UsedAt = DateTime.UtcNow
        };

        await repository.CreateAsync(usedToken);

        var result = await repository.GetByUserIdAsync(userId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByUserIdAsync_ExpiredToken()
    {
        var dbContext = GetDbContext("GetByUserIdAsync_ExpiredToken_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);

        var usedId = "userId";

        var usedToken = new EmailVerificationTokenEntity
        {
            Id = "userTokenId",
            UserId = "userId",
            Code = "444444",
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            IsUsed = true,
            UsedAt = DateTime.UtcNow
        };
        
        await repository.CreateAsync(usedToken);
        
        var result = await repository.GetByUserIdAsync(usedId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByUserIdAsync_NoTokens()
    {
        var dbContext = GetDbContext("GetByUserIdAsync_NoTokens_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);
        
        var result = await repository.GetByUserIdAsync("userId");
        
        Assert.Null(result);
    }
    
    [Fact]
    public async Task UpdateAsync_ShouldUpdateToken()
    {
        var dbContext = GetDbContext("UpdateAsync_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);

        var token = new EmailVerificationTokenEntity
        {
            Id = "tokenId",
            UserId = "userId",
            Code = "123456",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false,
        };

        await repository.CreateAsync(token);
        
        token.IsUsed = true;
        token.UsedAt = DateTime.UtcNow;
        
        var result = await repository.UpdateAsync(token);

        Assert.NotNull(result);
        Assert.True(result.IsUsed);
        Assert.NotNull(result.UsedAt);

        var fetchedToken = await repository.GetByCodeAsync(token.Code);
        Assert.NotNull(fetchedToken);
        Assert.True(fetchedToken.IsUsed);
        Assert.NotNull(fetchedToken.UsedAt);
    }

    [Fact]
    public async Task MarkAsUsedAsync_ShouldMarkTokenAsUsed()
    {
        var dbContext = GetDbContext("MarkAsUsedAsync_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);

        var token = new EmailVerificationTokenEntity
        {
            Id = "tokenId",
            UserId = "userId",
            Code = "123456",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false,
        };
        
        await repository.CreateAsync(token);

        await repository.MarkAsUsedAsync(token.Code);
        
        var updatedToken = await repository.GetByCodeAsync(token.Code);
        
        Assert.NotNull(updatedToken);
        Assert.True(updatedToken.IsUsed);
        Assert.NotNull(updatedToken.UsedAt);
    }

    [Fact]
    public async Task MarkAsUsedAsync_ShouldThrow_WhenNotFound()
    {
        var dbContext = GetDbContext("MarkAsUsedAsyncNotFound_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.MarkAsUsedAsync("nonExistingToken"));
    }

    [Fact]
    public async Task DeleteExpiredTokenAsync_ShouldDeleteExpiredToken()
    {
        var dbContext = GetDbContext("DeleteExpiredTokenAsync_Db");

        var repository = new EmailVerificationTokenRepository(dbContext);
        
        var validToken = new EmailVerificationTokenEntity
        {
            Id = "token_valid",
            UserId = "user_123",
            Code = "111111",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        };

        var expiredToken = new EmailVerificationTokenEntity
        {
            Id = "token_expired",
            UserId = "user_456",
            Code = "222222",
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            CreatedAt = DateTime.UtcNow.AddHours(-25),
            IsUsed = false
        };

        var usedToken = new EmailVerificationTokenEntity
        {
            Id = "token_used",
            UserId = "user_789",
            Code = "333333",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow,
            IsUsed = true,
            UsedAt = DateTime.UtcNow
        };
        
        await repository.CreateAsync(validToken);
        await repository.CreateAsync(expiredToken);
        await repository.CreateAsync(usedToken);

        await repository.DeletedExpiredTokensAsync();

        var remainingTokens = await dbContext.EmailVerificationTokens.ToListAsync();
        Assert.Single(remainingTokens);
        Assert.Equal(validToken.Code, remainingTokens.First().Code);
        
        
        // verify if expired and used token are deleted
        var expiredCheck = await repository.GetByCodeAsync(expiredToken.Code);
        var usedCheck = await repository.GetByCodeAsync(usedToken.Code);
        Assert.Null(expiredCheck);
        Assert.Null(usedCheck);
    }

    [Fact]
    public async Task DeleteByUserIdAsync_ShouldDeleteAllUserTokens()
    {
        var dbContext = GetDbContext("DeleteByUserId_Db");
        var repository = new EmailVerificationTokenRepository(dbContext);
        
        var userId = "userId";
        
        var token1 = new EmailVerificationTokenEntity
        {
            Id = "token_1",
            UserId = userId,
            Code = "111111",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        };

        var token2 = new EmailVerificationTokenEntity
        {
            Id = "token_2",
            UserId = userId,
            Code = "222222",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow,
            IsUsed = true,
            UsedAt = DateTime.UtcNow
        };

        var otherUserToken = new EmailVerificationTokenEntity
        {
            Id = "token_other",
            UserId = "user_456",
            Code = "333333",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        };

        await repository.CreateAsync(token1);
        await repository.CreateAsync(token2);
        await repository.CreateAsync(otherUserToken);

        await repository.DeleteByUserIdAsync(userId);
    
        var remainingTokens = await dbContext.EmailVerificationTokens.ToListAsync();
        
        Assert.Single(remainingTokens);
        Assert.Equal("user_456", remainingTokens.First().UserId);

        var deletedToken1 = await repository.GetByCodeAsync("111111");
        var deletedToken2 = await repository.GetByCodeAsync("222222");
        
        Assert.Null(deletedToken1);
        Assert.Null(deletedToken2);
    }
}