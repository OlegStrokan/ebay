using Application.Dtos;
using Domain.Entities.User;
using Domain.Repositories;
using System.Net.Mail;

namespace Application.UseCases.UpdateUser;

public class UpdateUserUseCase (IUserRepository repository) : IUpdateUserUseCase
{

    public async Task<UpdateUserResponse> ExecuteAsync(UpdateUserCommand command)
    {
        Validate(command);

        var existingUser = await repository.GetUserById(command.Id);

        if (existingUser == null)
        {
            throw new KeyNotFoundException($"User with ID {command.Id} not found");
        }

        var normalizedEmail = command.Email.Trim().ToLowerInvariant();
        var userWithEmail = await repository.GetUserByEmail(normalizedEmail);
        if (userWithEmail != null && userWithEmail.Id != existingUser.Id)
        {
            throw new InvalidOperationException($"User with email '{normalizedEmail}' already exists");
        }

        existingUser.Email = normalizedEmail;
        existingUser.Fullname = command.Fullname.Trim();
        existingUser.Phone = command.Phone.Trim();

        var countryCode = command.CountryCode.Trim().ToUpperInvariant();
        if (countryCode.Length != 2)
        {
            throw new ArgumentException("Country code must be a 2-letter ISO code", nameof(command.CountryCode));
        }
        existingUser.CountryCode = countryCode;

        if (command.CustomerTier.HasValue)
        {
            existingUser.CustomerTier = command.CustomerTier.Value;
        }

        if (command.CompanyId is not null)
        {
            existingUser.CompanyId = string.IsNullOrWhiteSpace(command.CompanyId)
                ? null
                : command.CompanyId;
        }

        var user = await repository.UpdateUser(existingUser);
        
        return new UpdateUserResponse(
            user.Id,
            user.Email,
            user.Fullname,
            user.Phone,
            user.CountryCode,
            user.CustomerTier,
            user.Status,
            user.CreatedAt,
            user.UpdatedAt,
            user.IsEmailVerified,
            user.DeliveryInfos.ToDtos(),
            user.UserRoles.Select(ur => ur.Role.Name).ToList(),
            user.CompanyId);
    }

    private static void Validate(UpdateUserCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Id))
        {
            throw new ArgumentException("User id is required", nameof(command.Id));
        }

        if (string.IsNullOrWhiteSpace(command.Fullname))
        {
            throw new ArgumentException("Full name is required", nameof(command.Fullname));
        }

        if (string.IsNullOrWhiteSpace(command.Email))
        {
            throw new ArgumentException("Email is required", nameof(command.Email));
        }

        if (string.IsNullOrWhiteSpace(command.Phone))
        {
            throw new ArgumentException("Phone is required", nameof(command.Phone));
        }

        try
        {
            _ = new MailAddress(command.Email.Trim());
        }
        catch (FormatException)
        {
            throw new ArgumentException("Email format is invalid", nameof(command.Email));
        }
    }
}