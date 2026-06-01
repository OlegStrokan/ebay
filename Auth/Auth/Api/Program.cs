using Api.GrpcServices;
using Application.Common.Interfaces;
using Application.UseCases.Login;
using Application.UseCases.RefreshToken;
using Application.UseCases.Register;
using Application.UseCases.RequestPasswordReset;
using Application.UseCases.ResendVerificationEmail;
using Application.UseCases.ResetPassword;
using Application.UseCases.RevokeToken;
using Application.UseCases.ValidateToken;
using Application.UseCases.VerifyEmail;
using Domain.Common.Interfaces;
using Domain.Gateways;
using Domain.Repositories;
using Infrastructure.DbContext;
using Infrastructure.Gateways;
using Infrastructure.Helpers;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Protos.User;

var builder = WebApplication.CreateBuilder(args);

var postgresConnectionString =
	builder.Configuration.GetConnectionString("Postgres")
	?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

var userServiceUrl =
	builder.Configuration["GrpcServices:UserUrl"]
	?? throw new InvalidOperationException("GrpcServices:UserUrl is required");

builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseNpgsql(postgresConnectionString));

builder.Services.AddGrpcClient<UserServiceProto.UserServiceProtoClient>(options =>
	options.Address = new Uri(userServiceUrl));

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<IJwtTokenGenerator>(sp => sp.GetRequiredService<JwtTokenService>());
builder.Services.AddSingleton<IJwtTokenValidator>(sp => sp.GetRequiredService<JwtTokenService>());
builder.Services.AddScoped<IIdGenerator, IdGenerator>();

// repositories
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddScoped<IEmailVerificationTokenRepository, EmailVerificationTokenRepository>();
builder.Services.AddScoped<IPasswordResetTokenRepository, PasswordResetTokenRepository>();

// use cases
builder.Services.AddScoped<IRegisterUseCase, RegisterUseCase>();
builder.Services.AddScoped<ILoginUseCase, LoginUseCase>();
builder.Services.AddScoped<IRefreshTokenUseCase, RefreshTokenUseCase>();
builder.Services.AddScoped<IRequestPasswordResetUseCase, RequestPasswordResetUseCase>();
builder.Services.AddScoped<IRevokeTokenUseCase, RevokeTokenUseCase>();
builder.Services.AddScoped<IValidateTokenUseCase,  ValidateTokenUseCase>();
builder.Services.AddScoped<IVerifyEmailUseCase, VerifyEmailUseCase>();
builder.Services.AddScoped<IResendVerificationEmailUseCase, ResendVerificationEmailUseCase>();
builder.Services.AddScoped<IResetPasswordUseCase, ResetPasswordUseCase>();

// external systems
builder.Services.AddScoped<IUserGateway, UserGateway>();
builder.Services.AddSingleton<IEmailGateway, EmailGateway>();

// api
builder.Services.AddGrpc();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	// TODO: delete it for long running testing
	await db.Database.EnsureDeletedAsync();  // DROP everything
	await db.Database.EnsureCreatedAsync();  // Recreate from current models
}

app.MapGrpcService<AuthGrpcService>();

app.MapGet("/", () => "Hello World!");

app.Run();

public partial class Program;