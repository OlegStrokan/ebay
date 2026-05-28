using Domain.Entities.Role;
using Domain.Repositories;

namespace Application.UseCases.AssignRole;

public class AssignRoleUseCase(IUserRepository userRepository, IRoleRepository roleRepository) : IAssignRoleUseCase
{
    // Privilege tiers: higher number = more privileged
    private static readonly Dictionary<string, int> PrivilegeTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User"] = 1,
        ["Seller"] = 2,
        ["Moderator"] = 3,
        ["Admin"] = 4,
        ["SuperAdmin"] = 5,
    };

    public async Task<AssignRoleResponse> ExecuteAsync(AssignRoleCommand command)
    {
        var user = await userRepository.GetUserById(command.UserId);
        if (user == null)
            throw new KeyNotFoundException($"User with ID {command.UserId} not found");

        var role = await roleRepository.GetByNameAsync(command.RoleName);
        if (role == null)
            throw new ArgumentException($"Role '{command.RoleName}' does not exist", nameof(command.RoleName));

        var alreadyAssigned = user.UserRoles.Any(ur => ur.Role.Name == command.RoleName);
        if (alreadyAssigned)
            throw new InvalidOperationException($"User {command.UserId} already has role '{command.RoleName}'");

        // Validate caller privilege: only users with a higher or equal tier can assign a role
        if (command.AssignedBy != "SYSTEM")
        {
            var caller = await userRepository.GetUserById(command.AssignedBy);
            if (caller == null)
                throw new UnauthorizedAccessException("Caller not found");

            var callerMaxTier = caller.UserRoles
                .Select(ur => PrivilegeTiers.GetValueOrDefault(ur.Role.Name, 0))
                .DefaultIfEmpty(0)
                .Max();

            var targetRoleTier = PrivilegeTiers.GetValueOrDefault(command.RoleName, int.MaxValue);

            if (callerMaxTier < targetRoleTier)
                throw new UnauthorizedAccessException(
                    $"Insufficient privileges to assign role '{command.RoleName}'. " +
                    $"Caller privilege level {callerMaxTier} is below required level {targetRoleTier}.");

            // Only Admin+ can assign roles at all
            if (callerMaxTier < PrivilegeTiers["Admin"])
                throw new UnauthorizedAccessException(
                    "Only users with Admin or SuperAdmin role can assign roles.");
        }

        var userRole = new UserRoleEntity
        {
            UserId = command.UserId,
            RoleId = role.Id,
            AssignedBy = command.AssignedBy,
            AssignedAt = DateTime.UtcNow,
        };

        await roleRepository.AssignRoleAsync(userRole);
        return new AssignRoleResponse(true);
    }
}
