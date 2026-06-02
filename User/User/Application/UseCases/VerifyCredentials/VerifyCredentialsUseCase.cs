using Application.Dtos;
using Domain.Common.Interfaces;
using Domain.Repositories;
using System.Net.Mail;

namespace Application.UseCases.VerifyCredentials;

public class VerifyCredentialsUseCase(
    IUserRepository repository,
    IPasswordHasher passwordHasher) : IVerifyCredentialsUseCase
{
    public async Task<VerifyCredentialsResponse?> ExecuteAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email is required", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required", nameof(password));
        }

        try
        {
            _ = new MailAddress(email.Trim());
        }
        catch (FormatException)
        {
            throw new ArgumentException("Email format is invalid", nameof(email));
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await repository.GetUserByEmail(normalizedEmail);

        if (user == null || !passwordHasher.VerifyPassword(password, user.Password))
        {
            return null;
        }

        return new VerifyCredentialsResponse(
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
}