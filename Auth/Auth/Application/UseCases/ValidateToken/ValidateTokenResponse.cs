namespace Application.UseCases.ValidateToken;

public record ValidateTokenResponse(
    bool IsValid,
    string? UserId,
    string? Message
);