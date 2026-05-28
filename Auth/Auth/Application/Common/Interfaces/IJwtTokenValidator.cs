namespace Application.Common.Interfaces;

public interface IJwtTokenValidator
{
    TokenValidationResult ValidateToken(string token);
}

public class TokenValidationResult
{
    public  required bool IsValid { get; init; }
    public string? UserId { get; init; }
    public string? Email { get; set; }
    public List<string> Roles { get; init; } = [];
    public string? Message { get; set; }
}