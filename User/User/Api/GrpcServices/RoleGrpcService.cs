using Domain.Entities.Role;
using Domain.Repositories;
using Grpc.Core;
using Protos.Role;

namespace Api.GrpcServices;

public class RoleGrpcService(IRoleRepository roleRepository) : RoleService.RoleServiceBase
{
    public override async Task<CreateRoleResponse> CreateRole(CreateRoleRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));

        var existing = await roleRepository.GetByNameAsync(request.Name);
        if (existing != null)
            throw new RpcException(new Status(StatusCode.AlreadyExists, $"Role '{request.Name}' already exists"));

        var role = new RoleEntity
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name.Trim(),
            Description = request.Name.Trim(),
            IsSystem = false,
        };

        var created = await roleRepository.CreateAsync(role);
        return new CreateRoleResponse { Role = new RoleProto { Id = created.Id, Name = created.Name } };
    }

    public override async Task<GetRoleResponse> GetRole(GetRoleRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "id is required"));

        var role = await roleRepository.GetByIdAsync(request.Id);
        if (role == null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Role with ID '{request.Id}' not found"));

        return new GetRoleResponse { Role = new RoleProto { Id = role.Id, Name = role.Name } };
    }

    public override async Task<GetAllRolesResponse> GetAllRoles(GetAllRolesRequest request, ServerCallContext context)
    {
        var roles = await roleRepository.GetAllAsync();
        var response = new GetAllRolesResponse();
        response.Roles.AddRange(roles.Select(r => new RoleProto { Id = r.Id, Name = r.Name }));
        return response;
    }

    public override async Task<UpdateRoleResponse> UpdateRole(UpdateRoleRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "id is required"));
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));

        var role = await roleRepository.GetByIdAsync(request.Id);
        if (role == null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Role with ID '{request.Id}' not found"));

        if (role.IsSystem)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Cannot modify system role '{role.Name}'"));

        role.Name = request.Name.Trim();
        var updated = await roleRepository.UpdateAsync(role);
        return new UpdateRoleResponse { Role = new RoleProto { Id = updated.Id, Name = updated.Name } };
    }

    public override async Task<DeleteRoleResponse> DeleteRole(DeleteRoleRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "id is required"));

        var role = await roleRepository.GetByIdAsync(request.Id);
        if (role == null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Role with ID '{request.Id}' not found"));

        if (role.IsSystem)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Cannot delete system role '{role.Name}'. System roles are protected."));

        await roleRepository.DeleteAsync(role);
        return new DeleteRoleResponse();
    }
}
