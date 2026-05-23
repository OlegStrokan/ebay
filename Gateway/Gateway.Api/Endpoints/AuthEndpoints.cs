using Gateway.Api.Contracts.Auth;

namespace Gateway.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/register", async (RegisterRequest request, Protos.Auth.AuthService.AuthServiceClient client) =>
        {
            var response = await client.RegisterAsync(new Protos.Auth.RegisterRequest
            {
                Email = request.Email,
                Password = request.Password,
                FullName = request.FullName,
                Phone = request.Phone
            });

            return Results.Created($"/api/v1/users/{response.UserId}",
                new RegisterResponse(response.UserId, response.Email, response.FullName, response.Message));
        });

        group.MapPost("/login", async (LoginRequest request, Protos.Auth.AuthService.AuthServiceClient client) =>
        {
            var response = await client.LoginAsync(new Protos.Auth.LoginRequest
            {
                Email = request.Email,
                Password = request.Password
            });

            return Results.Ok(new LoginResponse(
                response.AccessToken, response.RefreshToken, response.ExpiresIn, response.TokenType));
        });

        group.MapPost("/refresh", async (RefreshTokenRequest request, Protos.Auth.AuthService.AuthServiceClient client) =>
        {
            var response = await client.RefreshTokenAsync(new Protos.Auth.RefreshTokenRequest
            {
                RefreshToken = request.RefreshToken
            });

            return Results.Ok(new RefreshTokenResponse(response.AccessToken, response.ExpiresIn));
        });

        group.MapPost("/revoke", async (RevokeTokenRequest request, Protos.Auth.AuthService.AuthServiceClient client) =>
        {
            var response = await client.RevokeTokenAsync(new Protos.Auth.RevokeTokenRequest
            {
                RefreshToken = request.RefreshToken
            });

            return Results.Ok(new MessageResponse(response.Success, response.Message));
        }).RequireAuthorization();

        group.MapPost("/validate", async (ValidateTokenRequest request, Protos.Auth.AuthService.AuthServiceClient client) =>
        {
            var response = await client.ValidateTokenAsync(new Protos.Auth.ValidateTokenRequest
            {
                AccessToken = request.AccessToken
            });

            return Results.Ok(new ValidateTokenResponse(
                response.IsValid, response.UserId, response.Roles.ToList()));
        }).RequireAuthorization();
        group.MapPost("/verify-email", async (VerifyEmailRequest request, Protos.Auth.AuthService.AuthServiceClient client) =>
        {
            var response = await client.VerifyEmailAsync(new Protos.Auth.VerifyEmailRequest
            {
                Code = request.Code
            });

            return Results.Ok(new VerifyEmailResponse(response.Success, response.Message, response.UserId));
        });

        group.MapPost("/password-reset/request", async (
            RequestPasswordResetRequest request,
            HttpContext httpContext,
            Protos.Auth.AuthService.AuthServiceClient client) =>
        {
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var response = await client.RequestPasswordResetAsync(new Protos.Auth.RequestPasswordResetRequest
            {
                Email = request.Email,
                IpAddress = ipAddress
            });

            return Results.Ok(new MessageResponse(response.Success, response.Message));
        });

        group.MapPost("/password-reset/confirm", async (ResetPasswordRequest request, Protos.Auth.AuthService.AuthServiceClient client) =>
        {
            var response = await client.ResetPasswordAsync(new Protos.Auth.ResetPasswordRequest
            {
                Token = request.Token,
                NewPassword = request.NewPassword
            });

            return Results.Ok(new MessageResponse(response.Success, response.Message));
        });

        return group;
    }
}
