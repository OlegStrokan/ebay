using System.Security.Claims;
using Gateway.Api.Contracts.Common;
using Gateway.Api.Contracts.Users;
using GrpcUser = Protos.User;

namespace Gateway.Api.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/users")
            .WithTags("Users")
            .RequireAuthorization();

        group.MapPost("/", async (CreateUserRequest request, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            if (!Enum.TryParse<GrpcUser.CustomerTierProto>(request.CustomerTier, true, out var tier))
                tier = GrpcUser.CustomerTierProto.Standard;

            var response = await client.CreateUserAsync(new GrpcUser.CreateUserRequest
            {
                FullName = request.FullName,
                Password = request.Password,
                Email = request.Email,
                Phone = request.Phone,
                CountryCode = request.CountryCode,
                CustomerTier = tier
            });

            var user = MapUser(response.Data);
            return Results.Created($"/api/v1/users/{user.Id}", new ApiResponse<UserResponse>(user));
        });

        group.MapGet("/{id}", async (string id, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            var response = await client.GetUserByIdAsync(new GrpcUser.GetUserByIdRequest { Id = id });
            return Results.Ok(new ApiResponse<UserResponse>(MapUser(response.Data)));
        });

        group.MapPut("/{id}", async (string id, UpdateUserRequest request, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            if (!Enum.TryParse<GrpcUser.CustomerTierProto>(request.CustomerTier, true, out var tier))
                tier = GrpcUser.CustomerTierProto.Standard;

            var response = await client.UpdateUserAsync(new GrpcUser.UpdateUserRequest
            {
                Id = id,
                FullName = request.FullName,
                Email = request.Email,
                Phone = request.Phone,
                CountryCode = request.CountryCode,
                CustomerTier = tier
            });

            return Results.Ok(new ApiResponse<UserResponse>(MapUser(response.Data)));
        });

        group.MapDelete("/{id}", async (string id, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            await client.DeleteUserAsync(new GrpcUser.DeleteUserRequest { Id = id });
            return Results.NoContent();
        });

        group.MapPut("/{id}/password", async (string id, UpdatePasswordRequest request, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            await client.UpdatePasswordAsync(new GrpcUser.UpdatePasswordRequest
            {
                Id = id,
                CurrentPassword = request.CurrentPassword,
                NewPassword = request.NewPassword
            });

            return Results.NoContent();
        });

        group.MapPost("/{id}/block", async (string id, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            var response = await client.BlockUserAsync(new GrpcUser.BlockUserRequest { Id = id });
            return Results.Ok(new ApiResponse<UserResponse>(MapUser(response.Data)));
        });

        group.MapPost("/{id}/roles", async (string id, AssignRoleRequest request, HttpContext httpContext, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            // Inject assigned_by from JWT - never trust the client to supply it
            var callerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(callerId))
                return Results.Unauthorized();

            var response = await client.AssignRoleAsync(new GrpcUser.AssignRoleRequest
            {
                UserId = id,
                RoleName = request.RoleName,
                AssignedBy = callerId
            });

            return Results.Ok(new ApiResponse<AssignRoleResponse>(new AssignRoleResponse(response.Success)));
        });

        group.MapDelete("/{id}/roles/{roleName}", async (string id, string roleName, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            await client.RevokeRoleAsync(new GrpcUser.RevokeRoleRequest
            {
                UserId = id,
                RoleName = roleName
            });
            return Results.NoContent();
        });

        group.MapGet("/{id}/roles", async (string id, GrpcUser.UserServiceProto.UserServiceProtoClient client) =>
        {
            var response = await client.GetUserRolesAsync(new GrpcUser.GetUserRolesRequest { UserId = id });
            return Results.Ok(new ApiResponse<IReadOnlyList<string>>(response.Roles.ToList()));
        });

        return group;
    }

    private static UserResponse MapUser(GrpcUser.UserProto user) => new(
        user.Id,
        user.FullName,
        user.Email,
        user.Phone,
        user.Status.ToString(),
        user.CreatedAt,
        user.UpdatedAt,
        user.CountryCode,
        user.CustomerTier.ToString(),
        user.IsEmailVerified);
}
