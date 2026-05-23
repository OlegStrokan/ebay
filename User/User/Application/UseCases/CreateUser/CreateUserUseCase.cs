using Application.Dtos;
using Domain.Common.Interfaces;
using Domain.Entities.User;
using Domain.Repositories;
using System.Net.Mail;

namespace Application.UseCases.CreateUser;

public class CreateUserUseCase(
    IUserRepository userRepository,
    IPasswordHasher passwordHasher) : ICreateUserUseCase
{
    
    public async Task<CreateUserResponse> ExecuteAsync(CreateUserCommand command)
    {
        Validate(command);

        var normalizedEmail = command.Email.Trim().ToLowerInvariant();
        if (await userRepository.ExistsByEmail(normalizedEmail))
        {
            throw new InvalidOperationException($"User with email '{normalizedEmail}' already exists");
        }

        var now = DateTime.UtcNow;
        var userEntity = new UserEntity
        {
            Id = Guid.NewGuid().ToString(),
            Email = normalizedEmail,
            Password = passwordHasher.HashPassword(command.Password),
            Fullname = command.Fullname.Trim(),
            Phone = command.Phone.Trim(),
            CountryCode = command.CountryCode.Trim().ToUpperInvariant(),
            CustomerTier = command.CustomerTier,
            IsEmailVerified = command.AutoVerifyEmail,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var user = await userRepository.CreateUser(userEntity);

        return new CreateUserResponse(
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
            user.UserRoles.Select(ur => ur.Role.Name).ToList());
    }

    private static void Validate(CreateUserCommand command)
    {
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

        if (!string.IsNullOrWhiteSpace(command.CountryCode) && command.CountryCode.Trim().Length != 2)
        {
            throw new ArgumentException("Country code must be a 2-letter ISO code", nameof(command.CountryCode));
        }

        if (string.IsNullOrWhiteSpace(command.Password) || command.Password.Length < 8)
        {
            throw new ArgumentException("Password must be at least 8 characters long", nameof(command.Password));
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