using Grpc.Net.Client;
using Infrastructure.DbContext;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Protos.User;
using Testcontainers.PostgreSql;
using Xunit;

namespace User.E2ETests.Infrastructure;

[CollectionDefinition("E2E")]
public class E2ECollection : ICollectionFixture<E2ETestServer>
{
}

public sealed class E2ETestServer : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithDatabase("userdb_e2e")
        .WithUsername("test")
        .WithPassword("test")
        .WithImage("postgres:16-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await CreateSchemaAsync();
    }

    private async Task CreateSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var db = new AppDbContext(options);
        // Must migrate, not EnsureCreated: Program.cs migrates on startup, and EnsureCreated
        // builds the schema without stamping __EFMigrationsHistory, so that rerun would
        // fail with 42P07 "relation already exists".
        await db.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString()
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));
        });
    }

    public UserServiceProto.UserServiceProtoClient CreateUserClient()
    {
        var httpClient = CreateClient();
        var channel = GrpcChannel.ForAddress(
            httpClient.BaseAddress!,
            new GrpcChannelOptions { HttpClient = httpClient });

        return new UserServiceProto.UserServiceProtoClient(channel);
    }

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
