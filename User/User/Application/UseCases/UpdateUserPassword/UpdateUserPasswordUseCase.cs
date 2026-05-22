using Domain.Common.Interfaces;
using Domain.Repositories;

namespace Application.UseCases.UpdateUserPassword;

public class UpdateUserPasswordUseCase(
    IUserRepository repository,
    IPasswordHasher passwordHasher) : IUpdateUserPasswordUseCase
{
    public async Task<UpdateUserPasswordResult> ExecuteAsync(UpdateUserPasswordCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.UserId))
        {
            throw new ArgumentException("User id is required", nameof(command.UserId));
        }

        if (string.IsNullOrWhiteSpace(command.NewPassword))
        {
            throw new ArgumentException("New password is required", nameof(command.NewPassword));
        }

        var user = await repository.GetUserById(command.UserId);
        if (user == null)
        {
            return new UpdateUserPasswordResult(false, $"User with ID {command.UserId} not found");
        }

        user.Password = passwordHasher.HashPassword(command.NewPassword);
        await repository.UpdateUser(user);

        return new UpdateUserPasswordResult(true, "Password updated successfully");
    }
}
