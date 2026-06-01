namespace Domain.Gateways;


/*
/ this is a fucking interface for foocking user microservice, ol rait?
/ this abstracts external user service, treating it like an own whore
/ as old guy said: the application layer depends on interfaces, not on own wife's opinion 
*/
public interface IUserGateway
{
    Task<string> CreateUserAsync(string email, string hashedPassword, string fullName, string phone);
    Task<UserGatewayDto?> GetUserByEmailAsync(string email);
    Task<UserGatewayDto?> VerifyCredentialsAsync(string email, string password);
    Task<UserGatewayDto?> GetUserByIdAsync(string userId);
    Task<bool> VerifyUserEmailAsync(string userId);
    Task<bool> UpdateUserPasswordAsync(string userId, string newPassword);
}




// dto for user data from external services
// domain-level, not tied to any specific protol
public class UserGatewayDto
{
    public required string Id { get; set; }
    public required string Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public required string FullName { get; set; }
    public required string Phone { get; set; }
    public bool IsEmailVerified { get; set; }
    public UserStatus Status { get; set; }
    public IReadOnlyList<string> Roles { get; set; } = [];
    public string? CompanyId { get; set; }
}

public enum UserStatus
{
    Active,
    Restricted,
    Banned
}