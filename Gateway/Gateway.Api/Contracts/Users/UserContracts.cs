namespace Gateway.Api.Contracts.Users;

public sealed record CreateUserRequest(
    string FullName,
    string Password,
    string Email,
    string Phone,
    string CountryCode,
    string CustomerTier);

public sealed record UpdateUserRequest(
    string FullName,
    string Email,
    string Phone,
    string CountryCode,
    string CustomerTier);

public sealed record UpdatePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UserResponse(
    string Id,
    string FullName,
    string Email,
    string Phone,
    string Status,
    long CreatedAt,
    long UpdatedAt,
    string CountryCode,
    string CustomerTier,
    bool IsEmailVerified);

public sealed record AssignRoleRequest(string RoleName);
public sealed record AssignRoleResponse(bool Success);
