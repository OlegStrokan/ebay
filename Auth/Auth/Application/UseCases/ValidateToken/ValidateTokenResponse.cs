namespace Application.UseCases.ValidateToken;

public record ValidateTokenResponse(
    bool IsValid,
    string? UserId,
    List<string> Roles,
    string? Message
);