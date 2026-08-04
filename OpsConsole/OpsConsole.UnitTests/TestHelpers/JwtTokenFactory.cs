using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace OpsConsole.UnitTests.TestHelpers;

// Mints real, signed JWTs against the same secret the test host is configured with
// (see OpsConsoleWebApplicationFactory), rather than swapping in a fake auth scheme —
// OpsViewer/OpsAdmin are real RequireRole policies evaluated by the real JwtBearer
// handler, so the test needs a real token for that check to mean anything.
public static class JwtTokenFactory
{
    public static string CreateToken(string secretKey, string audience, string issuer, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "test-operator-id"),
            new(ClaimTypes.Email, "operator@test.local"),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
