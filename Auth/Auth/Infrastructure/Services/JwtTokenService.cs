using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using TokenValidationResult = Application.Common.Interfaces.TokenValidationResult;

namespace Infrastructure.Services;

public class JwtTokenService : IJwtTokenGenerator, IJwtTokenValidator
{
    private readonly RsaSecurityKey _signingKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _accessTokenExpirationMinutes;

    public JwtTokenService(IConfiguration configuration)
    {
        var privateKeyBase64 = configuration["Jwt:PrivateKeyBase64"] ??
                     throw new InvalidOperationException("JWT private key (Jwt:PrivateKeyBase64) not configured");
        _issuer = configuration["Jwt:Issuer"] ?? "AuthService";
        _audience = configuration["Jwt:Audience"] ?? "ApiGateway";
        _accessTokenExpirationMinutes = int.Parse(configuration["Jwt:AccessTokenExpirationMinutes"] ?? "60");

        // RS256: Auth is the ONLY holder of the private key. Verifiers (Gateway, OpsConsole)
        // get the public key only, so a compromised verifier can check tokens but never mint them.
        var rsa = RSA.Create();
        rsa.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(privateKeyBase64)));
        _signingKey = new RsaSecurityKey(rsa);
    }

    public string GenerateAccessToken(string userId, string email, IEnumerable<string> roles, string? companyId = null)
    {
        var tokenHandler = new JwtSecurityTokenHandler();

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, email),
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!string.IsNullOrEmpty(companyId))
        {
            claims.Add(new Claim("company_id", companyId));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_accessTokenExpirationMinutes),
            Issuer = _issuer,
            Audience = _audience,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);

    }

    public string GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    public TokenValidationResult ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TokenValidationResult
            {
                IsValid = false,
                Message = "Token is null or empty"
            };
        }

        var tokenHandler = new JwtSecurityTokenHandler();

        try
        {
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _signingKey,
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero // no tolerance for expired tokens
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            // extract claims 

            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                         principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            var email = principal.FindFirst(ClaimTypes.Email)?.Value ??
                        principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value;

            var roles = principal.FindAll(ClaimTypes.Role)
                .Select(c => c.Value)
                .ToList();

            return new TokenValidationResult
            {
                IsValid = true,
                UserId = userId,
                Email = email,
                Roles = roles,
            };
        }
        catch (SecurityTokenExpiredException)
        {
            return new TokenValidationResult
            {
                IsValid = false,
                Message = "Token has expired"
            };
        }

        catch (SecurityTokenInvalidSignatureException)
        {
            return new TokenValidationResult
            {
                IsValid = false,
                Message = "Invalid token signature"
            };
        }

        catch (Exception ex)
        {
            return new TokenValidationResult
            {
                IsValid = false,
                Message = $"Token validation failed: {ex.Message}"
            };
        }

        


    }
}