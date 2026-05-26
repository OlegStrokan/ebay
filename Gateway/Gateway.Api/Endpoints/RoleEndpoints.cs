using Gateway.Api.Contracts.Common;
using Gateway.Api.Contracts.Roles;
using GrpcRole = Protos.Role;

namespace Gateway.Api.Endpoints;

public static class RoleEndpoints
{
    public static RouteGroupBuilder MapRoleEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/roles")
            .WithTags("Roles")
            .RequireAuthorization();

        group.MapPost("/", async (CreateRoleRequest request, GrpcRole.RoleService.RoleServiceClient client) =>
        {
            var response = await client.CreateRoleAsync(new GrpcRole.CreateRoleRequest { Name = request.Name });
            var role = MapRole(response.Role);
            return Results.Created($"/api/v1/roles/{role.Id}", new ApiResponse<RoleResponse>(role));
        });

        group.MapGet("/", async (GrpcRole.RoleService.RoleServiceClient client) =>
        {
            var response = await client.GetAllRolesAsync(new GrpcRole.GetAllRolesRequest());
            return Results.Ok(new ApiResponse<IReadOnlyList<RoleResponse>>(response.Roles.Select(MapRole).ToList()));
        });

        group.MapGet("/{id}", async (string id, GrpcRole.RoleService.RoleServiceClient client) =>
        {
            var response = await client.GetRoleAsync(new GrpcRole.GetRoleRequest { Id = id });
            return Results.Ok(new ApiResponse<RoleResponse>(MapRole(response.Role)));
        });

        group.MapPut("/{id}", async (string id, UpdateRoleRequest request, GrpcRole.RoleService.RoleServiceClient client) =>
        {
            var response = await client.UpdateRoleAsync(new GrpcRole.UpdateRoleRequest { Id = id, Name = request.Name });
            return Results.Ok(new ApiResponse<RoleResponse>(MapRole(response.Role)));
        });

        group.MapDelete("/{id}", async (string id, GrpcRole.RoleService.RoleServiceClient client) =>
        {
            await client.DeleteRoleAsync(new GrpcRole.DeleteRoleRequest { Id = id });
            return Results.NoContent();
        });

        return group;
    }

    private static RoleResponse MapRole(GrpcRole.RoleProto role) => new(role.Id, role.Name);
}
