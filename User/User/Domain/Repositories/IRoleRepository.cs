using Domain.Entities.Role;

namespace Domain.Repositories;

public interface IRoleRepository
{
    Task<IReadOnlyList<RoleEntity>> GetAllAsync();
    Task<RoleEntity?> GetByIdAsync(string id);
    Task<RoleEntity?> GetByNameAsync(string name);
    Task<IReadOnlyList<RoleEntity>> GetUserRolesAsync(string userId);
    Task AssignRoleAsync(UserRoleEntity userRole);
    Task RevokeRoleAsync(string userId, string roleName);
    Task<RoleEntity> CreateAsync(RoleEntity role);
    Task<RoleEntity> UpdateAsync(RoleEntity role);
    Task DeleteAsync(RoleEntity role);
}
