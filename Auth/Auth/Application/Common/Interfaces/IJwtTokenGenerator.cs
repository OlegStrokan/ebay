namespace Application.Common.Interfaces;

public interface IJwtTokenGenerator
{
    string GenerateAccessToken(string userId, string email, IEnumerable<string> roles, string? companyId = null);
    string GenerateRefreshToken();
}