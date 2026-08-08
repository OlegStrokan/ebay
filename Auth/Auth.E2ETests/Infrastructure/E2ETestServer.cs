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
      
        await db.Database.MigrateAsync();
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
                ["Jwt:PrivateKeyBase64"] = "LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0tCk1JSUV2UUlCQURBTkJna3Foa2lHOXcwQkFRRUZBQVNDQktjd2dnU2pBZ0VBQW9JQkFRQ2hYa2U3VHl3U2NjYm4KdDhkK21IbEZxS3pqaDdLNmJkbTZ1Z1N5b1BQRWlCWWFJREo0NEdyTjNFYkYrV0l2Z0gwUml6cFR5bUFxd1lXLwpkL1EzNk83Y2VocWNyaGpQL2RkRXN5UU5FSTNtQjhod1A3NXZGSGNxcitoNTQ2L1ZZbmVwMTRkWEN3aU9iME9UCnJLMkgwZndOWnRhQ2Qxczd0aGZJUG91NUIvWThnT3IwYkR3cXlwVkR3YStxbnRVbG1XOHV6SGFHSnR0NnBtajUKYXVQdTE4endvTXRyTm5wckZKUHVyVGVMTC9NSEdXa29vNTJlM1p3djF4NkRSNGdGZ2M4L2hvbTdxNStwWTVPVQpJeldaWlBVckhhdTF3Z0MvWUlnNnJudFE4ZHhOeWtGOVpXQ25vRkZBVW5URnhDQ0F2a0tLaUtKZmhwZlpKVmpDCk16TWwwSTl6QWdNQkFBRUNnZ0VBSU8xNUc2cWJKcVJhM3h1c05KUHVZeDE1TWZDVnN0OEpoOFcvZ2FmQU5rRkMKcVZBYW5IbkdzWDBhWC9sMFpKY0dibGNIcnVOajNqV2hFaUhyRHFHVVpCN3lZVGhSVGRmUlhtNWprOXJsNmFONgo3aFREeWl6VjZEcis2Q2hpejlzSTZmcFYzcGdjeGR2RVlWVGlFQTMwTGRQblA3WVZRc2owYjJMNzVlVFBCU2RCCjU0MGEybUFRZkhTUGhzSXduQzNsN3hRVHVGZXI5SWNjL0hpZ3h4ZURUTm5NSFRKUGlDTzVjK1BUUWdxSXBPUUIKeVhrbm5tTzFyOUNOZ0MxY3g4R1hMQldydDI2MlM5ckRLSHk1UysxQUkzTk9adW0xUXlJNWNvM3dsT3crelhjRgoxZlR0QndkTlFZajV4eFlFZTkzMUM5SG9vcGRPQ2RjMlZYZXhWSUUwV1FLQmdRRGhPa29UbktVdmFXOCs1NkdyCmxKcjRobU5IZlJzOS8vNitGTnhEUzQ1LzFCSnVMSU8vcTdydGZWdmpVRVJNOG1Ua3JDWi9JcGVlZ1QwWVo4Q0gKekhLazlPOUxNOE1RUXgrYXRLdXphOEE4aXB1Q01LZFVlYkFkbkZxNldpR1l6cUh1YmZMdEt3U1JFZ3h2RW1UWAovK1EyRjVrUGtsd2dtYjhDU3FGbFBSOVRCd0tCZ1FDM2FtY0hBZWZiQ1YvdnlmRjZZWnJ2eHNDZEtFS2JnT25WCjBja0NUNkFxTlBGRzdKckpad1R1U3p5aDd6SFFaZVhDZGxoT0pDYlRHUTB6bUxtN0UyK2czcUNEWjlYcmhpMEUKOTIrcCs3OEZ3cE1XWkU5ZTBvMnk4ZlNxNmZqTGRNa0xNcDRQTFpSZ0RDZjdHQ0JBM2hIbzRxeTNtcHc0alNkdQpMQnR0ZG1YcE5RS0JnQzZkWGMrSlVEYnIzM1pwZ25COHBVWmlxaEdWdHhteDdndHhUZFV2d2lKNnhnVy9lTlVtCnVkMkZZSXMvaGFOWFY4SnNUdHRwVVhBZzE0QkJtUHVDT1FnakdaTzY5dGhhekNPODJQeWRoSUFEUUFSR0JadmEKUTdVZE16bjJoWldXenJVR1ZJejVwa3hRSy9xaEYvWU1wREw5MTFQOXVzdVVoby8yMmtpVnlmSHBBb0dBWG83cApmTEJiMHcyN093azJpQ3huenpQOU8waDFSbXdvb1laZEJlYjlJS1ZZdW9MaXJmQ0JsMFNNaHNPbFA5WTRwSStVCnFQeDBVNkozcnVFTzU4WjJaMDQvSEYvYzVtYXZNUDlMdnl1OWFIL09pdDIrR1ptZFdlTHBpMi9DUjBuM0YrSEoKb1BPVHFneTZVL1kxTXB3S1NiRUs4RUV5UnVsbXFhTHRwUHBFUWYwQ2dZRUFueXVWcTJmN3h6akZoMWNheTNuQQpzaVdvcm4ydHl0dGNSd1MxaVJXa2dVRUdxWnc3Rm5GZjc0YnB1dXRWL3dGYnZSVWUxU1FFR1p6NzFESmlpNmZhCllTeFFVMk9PRFlDSVpqVVJPT2htZ29GbHBJN2hrSU9jWVdKYlhIMmRlN0ZkamRtZDRacG95WjAvR2tNTVV0S1QKMWZKSEptSjVlZ29HMFlxRFhOa1pvUXM9Ci0tLS0tRU5EIFBSSVZBVEUgS0VZLS0tLS0K",
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
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password), // Auth now sends plaintext; the real User service hashes
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

    public (bool Success, string Message) UpdatePassword(string userId, string newPassword)
    {
        lock (_lock)
        {
            if (!_usersById.TryGetValue(userId, out var user))
            {
                return (false, $"User with ID {userId} not found");
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
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
        var result = store.UpdatePassword(request.UserId, request.NewPassword);

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
