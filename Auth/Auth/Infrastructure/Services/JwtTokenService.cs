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
    private readonly string _secretKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _accessTokenExpirationMinutes;

    public JwtTokenService(IConfiguration configuration)
    {
        _secretKey = configuration["Jwt:SecretKey"] ??
                     throw new InvalidOperationException("JWT Secret Key not configured");
        _issuer = configuration["Jwt:Issuer"] ?? "AuthService";
        _audience = configuration["Jwt:Audience"] ?? "ApiGateway";
        _accessTokenExpirationMinutes = int.Parse(configuration["Jwt:AccessTokenExpirationMinutes"] ?? "60");

        if (_secretKey.Length < 32)
        {
            throw new InvalidOperationException(
                "JWT Secret Key should be at least 32 characters long");
        }
    }

    public string GenerateAccessToken(string userId, string email, IEnumerable<string> roles, string? companyId = null)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_secretKey);


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
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
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
        var key = Encoding.UTF8.GetBytes(_secretKey);

        try
        {
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
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