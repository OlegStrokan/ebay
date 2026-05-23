namespace Gateway.Api.Contracts.Auth;

public sealed record RegisterRequest(string Email, string Password, string FullName, string Phone);
public sealed record RegisterResponse(string UserId, string Email, string FullName, string Message);

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn, string TokenType);

public sealed record RefreshTokenRequest(string RefreshToken);
public sealed record RefreshTokenResponse(string AccessToken, int ExpiresIn);

public sealed record RevokeTokenRequest(string RefreshToken);
public sealed record MessageResponse(bool Success, string Message);

public sealed record ValidateTokenRequest(string AccessToken);
public sealed record ValidateTokenResponse(bool IsValid, string UserId, IReadOnlyList<string> Roles);

public sealed record VerifyEmailRequest(string Code);
public sealed record VerifyEmailResponse(bool Success, string Message, string UserId);

public sealed record RequestPasswordResetRequest(string Email);
public sealed record ResetPasswordRequest(string Token, string NewPassword);
