using Api.GrpcServices;
using Api.Middleware;
using Application;
using Infrastructure;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<ExceptionHandlingInterceptor>();
});

builder.Services.AddSingleton<ExceptionHandlingInterceptor>();
builder.Services.AddGrpcHealthChecks();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
    await db.Database.MigrateAsync();
}

app.MapGrpcService<AccountingGrpcService>();
app.MapGrpcHealthChecksService();

app.Run();

public partial class Program;
