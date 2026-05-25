using Infrastructure.DbContext;
using Grpc.Net.Client;
using System.Net;
using System.Net.Http;
using Application.Common.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Protos.Auth;
using Protos.User;
using Testcontainers.PostgreSql;
using Xunit;
using Grpc.Core;

namespace Auth.E2ETests.Infrastructure;

[CollectionDefinition("E2E")]
public class E2ECollection : ICollectionFixture<E2ETestServer>
{
}

public sealed class E2ETestServer : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithDatabase("authdb_e2e")
        .WithUsername("test")
        .WithPassword("test")
        .WithImage("postgres:16-alpine")
        .Build();

    private WebApplication? _fakeUserApp;
    private readonly FakeUserStore _fakeUserStore = new();
    private string _fakeUserUrl = "http://127.0.0.1:50095";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await StartFakeUserServiceAsync();
        await CreateSchemaAsync();
    }

    private async Task StartFakeUserServiceAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // gRPC over cleartext requires an h2c endpoint explicitly set to HTTP/2.
            options.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_fakeUserStore);

        var app = builder.Build();
        app.MapGrpcService<FakeUserGrpcService>();
        await app.StartAsync();

        var addressesFeature = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        _fakeUserUrl = addressesFeature?.Addresses.Single()
            ?? throw new InvalidOperationException("Failed to resolve fake user gRPC endpoint address");

        _fakeUserApp = app;
    }

    private async Task CreateSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                ["GrpcServices:UserUrl"] = _fakeUserUrl,
                ["Jwt:SecretKey"] = "e2e_super_secret_auth_key_which_is_long_enough_32",
                ["Jwt:Issuer"] = "AuthService",
                ["Jwt:Audience"] = "ApiGateway",
                ["Jwt:AccessTokenExpirationMinutes"] = "60"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));

            services.RemoveAll<UserServiceProto.UserServiceProtoClient>();
            services.AddSingleton(_ =>
            {
                var channel = GrpcChannel.ForAddress(_fakeUserUrl, new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        // Avoid environment proxy settings interfering with local gRPC h2c traffic.
                        UseProxy = false,
                        EnableMultipleHttp2Connections = true
                    }
                });

                return new UserServiceProto.UserServiceProtoClient(channel);
            });

            services.RemoveAll<IEmailGateway>();
            services.AddSingleton<IEmailGateway, NoOpEmailGateway>();
        });
    }

    public AuthService.AuthServiceClient CreateAuthClient()
    {
        var httpClient = CreateClient();
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });

        return new AuthService.AuthServiceClient(channel);
    }

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();

        if (_fakeUserApp is not null)
        {
            await _fakeUserApp.StopAsync();
            await _fakeUserApp.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}

internal sealed class NoOpEmailGateway : IEmailGateway
{
    public Task SendVerificationEmailAsync(string recipientEmail, string verificationCode, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendPasswordResetEmailAsync(string recipientEmail, string resetToken, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class FakeUserStore
{
    private readonly Dictionary<string, FakeUserRecord> _usersById = new();
    private readonly object _lock = new();

    public FakeUserRecord? GetById(string id)
    {
        lock (_lock)
        {
            return _usersById.TryGetValue(id, out var user) ? user : null;
        }
    }

    public FakeUserRecord? GetByEmail(string email)
    {
        var normalizedEmail = NormalizeEmail(email);

        lock (_lock)
        {
            return _usersById.Values.FirstOrDefault(u => u.Email == normalizedEmail);
        }
    }

    public FakeUserRecord? VerifyCredentials(string email, string password)
    {
        var user = GetByEmail(email);
        if (user == null)
        {
            return null;
        }

        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash) ? user : null;
    }

    public FakeUserRecord Create(string email, string password, string fullName, string phone)
    {
        var normalizedEmail = NormalizeEmail(email);

        lock (_lock)
        {
            if (_usersById.Values.Any(u => u.Email == normalizedEmail))
            {
                throw new InvalidOperationException($"User with email {normalizedEmail} already exists");
            }

            var now = DateTime.UtcNow;
            var user = new FakeUserRecord
            {
                Id = Guid.NewGuid().ToString("N")[..26],
                Email = normalizedEmail,
                FullName = fullName.Trim(),
                Phone = phone.Trim(),
                PasswordHash = password, // already BCrypt-hashed by RegisterUseCase
                Status = UserStatusProto.Active,
                IsEmailVerified = false,
                CreatedAt = now,
                UpdatedAt = now,
            };

            _usersById[user.Id] = user;
            return user;
        }
    }

    public bool VerifyEmail(string userId)
    {
        lock (_lock)
        {
            if (!_usersById.TryGetValue(userId, out var user))
            {
                return false;
            }

            user.IsEmailVerified = true;
            user.UpdatedAt = DateTime.UtcNow;
            return true;
        }
    }

    public (bool Success, string Message) UpdatePassword(string userId, string newPasswordHash)
    {
        lock (_lock)
        {
            if (!_usersById.TryGetValue(userId, out var user))
            {
                return (false, $"User with ID {userId} not found");
            }

            user.PasswordHash = newPasswordHash;
            user.UpdatedAt = DateTime.UtcNow;
            return (true, "Password updated successfully");
        }
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

internal sealed class FakeUserRecord
{
    public required string Id { get; init; }
    public required string Email { get; set; }
    public required string FullName { get; set; }
    public required string Phone { get; set; }
    public required string PasswordHash { get; set; }
    public required UserStatusProto Status { get; set; }
    public bool IsEmailVerified { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

internal sealed class FakeUserGrpcService(FakeUserStore store) : UserServiceProto.UserServiceProtoBase
{
    public override Task<CreateUserResponse> CreateUser(CreateUserRequest request, ServerCallContext context)
    {
        try
        {
            var user = store.Create(request.Email, request.Password, request.FullName, request.Phone);

            return Task.FromResult(new CreateUserResponse
            {
                Data = ToUserProto(user)
            });
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }
    }

    public override Task<GetUserByEmailResponse> GetUserByEmail(GetUserByEmailRequest request, ServerCallContext context)
    {
        var user = store.GetByEmail(request.Email);
        if (user == null)
        {
            return Task.FromResult(new GetUserByEmailResponse());
        }

        return Task.FromResult(new GetUserByEmailResponse
        {
            Data = ToUserProto(user),
        });
    }

    public override Task<VerifyCredentialsResponse> VerifyCredentials(VerifyCredentialsRequest request, ServerCallContext context)
    {
        var user = store.VerifyCredentials(request.Email, request.Password);
        if (user == null)
        {
            return Task.FromResult(new VerifyCredentialsResponse());
        }

        return Task.FromResult(new VerifyCredentialsResponse
        {
            Data = ToUserProto(user),
            IsValid = true,
        });
    }

    public override Task<GetUserByIdResponse> GetUserById(GetUserByIdRequest request, ServerCallContext context)
    {
        var user = store.GetById(request.Id);
        return Task.FromResult(new GetUserByIdResponse
        {
            Data = user == null ? null : ToUserProto(user)
        });
    }

    public override Task<VerifyUserEmailResponse> VerifyUserEmail(VerifyUserEmailRequest request, ServerCallContext context)
    {
        var success = store.VerifyEmail(request.UserId);
        return Task.FromResult(new VerifyUserEmailResponse { Success = success });
    }

    public override Task<UpdateUserPasswordResponse> UpdateUserPassword(UpdateUserPasswordRequest request, ServerCallContext context)
    {
        var result = store.UpdatePassword(request.UserId, request.NewPasswordHash);

        return Task.FromResult(new UpdateUserPasswordResponse
        {
            Success = result.Success,
            Message = result.Message,
        });
    }

    private static UserProto ToUserProto(FakeUserRecord user)
    {
        return new UserProto
        {
            Id = user.Id,
            Email = user.Email,
            FullName = user.FullName,
            Phone = user.Phone,
            Status = user.Status,
            IsEmailVerified = user.IsEmailVerified,
            CreatedAt = new DateTimeOffset(user.CreatedAt).ToUnixTimeSeconds(),
            UpdatedAt = new DateTimeOffset(user.UpdatedAt).ToUnixTimeSeconds(),
        };
    }
}
