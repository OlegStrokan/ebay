namespace Application.UseCases.CreateUser;

using Domain.Entities.User;

public record CreateUserCommand(
	string Email,
	string Password,
	string Fullname,
	string Phone,
	string CountryCode = "DE",
	CustomerTier CustomerTier = CustomerTier.Standard,
	bool AutoVerifyEmail = false);