namespace Application.UseCases.UpdateUserPassword;

public record UpdateUserPasswordCommand(
    string UserId,
    string NewPassword);
