namespace Application.UseCases.UpdateUser;

using Domain.Entities.User;

public record UpdateUserCommand(
    string Id,
    string Email,
    string Fullname,
    string Phone,
    string CountryCode = "",
    CustomerTier? CustomerTier = null,
    string? CompanyId = null);