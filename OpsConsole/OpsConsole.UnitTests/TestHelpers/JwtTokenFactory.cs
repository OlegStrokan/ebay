using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace OpsConsole.UnitTests.TestHelpers;

// Mints real, signed JWTs with the dev RSA private key (RS256). The OpsConsole host under
// test validates them with the matching PUBLIC key, exactly as production validates
// Auth-issued tokens — OpsViewer/OpsAdmin are real RequireRole policies, so the test needs
// a real token for that check to mean anything.
public static class JwtTokenFactory
{
    public static string CreateToken(string privateKeyBase64, string audience, string issuer, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "test-operator-id"),
            new(ClaimTypes.Email, "operator@test.local"),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var rsa = RSA.Create();
        rsa.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(privateKeyBase64)));
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
