using Api.GrpcServices;
using Domain.Common.Interfaces;
using Application.UseCases.AssignRole;
using Application.UseCases.RestrictUser;
using Application.UseCases.LiftRestriction;
using Application.UseCases.CreateUser;
using Application.UseCases.DeleteUser;
using Application.UseCases.GetAllRoles;
using Application.UseCases.GetUserByEmail;
using Application.UseCases.GetUserById;
using Application.UseCases.GetUserRoles;
using Application.UseCases.RevokeRole;
using Application.UseCases.UpdatePassword;
using Application.UseCases.UpdateUserPassword;
using Application.UseCases.UpdateUser;
using Application.UseCases.VerifyCredentials;
using Application.UseCases.VerifyUserEmail;
using Infrastructure.DbContext;
using Microsoft.EntityFrameworkCore;
using Domain.Repositories;
using Infrastructure.Helpers;
using Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

var postgresConnectionString =
	builder.Configuration.GetConnectionString("Postgres")
	?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseNpgsql(postgresConnectionString));

builder.Services.AddScoped<ICreateUserUseCase, CreateUserUseCase>();
builder.Services.AddScoped<IUpdateUserUseCase, UpdateUserUseCase>();
builder.Services.AddScoped<IGetUserByIdUseCase, GetUserByIdUseCase>();
builder.Services.AddScoped<IGetUserByEmailUseCase, GetUserByEmailUseCase>();
builder.Services.AddScoped<IVerifyCredentialsUseCase, VerifyCredentialsUseCase>();
builder.Services.AddScoped<IDeleteUserUseCase, DeleteUserUseCase>();
builder.Services.AddScoped<IRestrictUserUseCase, RestrictUserUseCase>();
builder.Services.AddScoped<ILiftRestrictionUseCase, LiftRestrictionUseCase>();
builder.Services.AddScoped<IUpdatePasswordUseCase, UpdatePasswordUseCase>();
builder.Services.AddScoped<IVerifyUserEmailUseCase, VerifyUserEmailUseCase>();
builder.Services.AddScoped<IUpdateUserPasswordUseCase, UpdateUserPasswordUseCase>();
builder.Services.AddScoped<IAssignRoleUseCase, AssignRoleUseCase>();
builder.Services.AddScoped<IRevokeRoleUseCase, RevokeRoleUseCase>();
builder.Services.AddScoped<IGetUserRolesUseCase, GetUserRolesUseCase>();
builder.Services.AddScoped<IGetAllRolesUseCase, GetAllRolesUseCase>();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IRoleRepository, RoleRepository>();
builder.Services.AddScoped<IUserRestrictionRepository, UserRestrictionRepository>();

builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();

builder.Services.AddGrpc();


var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	await db.Database.MigrateAsync();
}

app.MapGrpcService<UserGrpcService>();
app.MapGrpcService<RoleGrpcService>();



app.MapGet("/", () => "Hello World!");

app.Run();

public partial class Program;