namespace Application.UseCases.Register;

public record RegisterResponse(string UserId, string Email, string Fullname, string VerificationCode, string Message);