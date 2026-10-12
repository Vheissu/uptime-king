using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace UptimeKing.Api.Endpoints;

public record LoginInput(string Password);

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet(
                "/api/session",
                (HttpContext c) =>
                    Results.Ok(new { Authenticated = c.User.Identity?.IsAuthenticated == true })
            )
            .AllowAnonymous();
        app.MapPost(
                "/api/session",
                async (LoginInput input, HttpContext context, IConfiguration config) =>
                {
                    var expected = config["Auth:Password"];
                    if (string.IsNullOrEmpty(expected))
                        return Results.Problem(
                            "Workspace sign-in has not been configured.",
                            statusCode: 503
                        );
                    if (
                        input.Password is null
                        || input.Password.Length > 1024
                        || !CryptographicOperations.FixedTimeEquals(
                            SHA256.HashData(Encoding.UTF8.GetBytes(input.Password)),
                            SHA256.HashData(Encoding.UTF8.GetBytes(expected))
                        )
                    )
                        return Results.Json(
                            new { error = "That password doesn't match. Try again." },
                            statusCode: 401
                        );
                    var identity = new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.Name, "Workspace operator"),
                            new Claim(PasswordStamp.ClaimType, PasswordStamp.Create(expected)),
                        ],
                        CookieAuthenticationDefaults.AuthenticationScheme
                    );
                    await context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(identity)
                    );
                    return Results.NoContent();
                }
            )
            .AllowAnonymous()
            .RequireRateLimiting("login");
        app.MapDelete(
                "/api/session",
                async (HttpContext c) =>
                {
                    await c.SignOutAsync();
                    return Results.NoContent();
                }
            )
            .RequireAuthorization();
    }
}

// The stamp is inside the encrypted authentication ticket, never returned by the session API.
public static class PasswordStamp
{
    public const string ClaimType = "uptime-king:password-version";

    public static string Create(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    public static bool Matches(ClaimsPrincipal? principal, string? password)
    {
        var stamp = principal?.FindFirstValue(ClaimType);
        return !string.IsNullOrEmpty(password)
            && stamp is not null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(stamp),
                Encoding.UTF8.GetBytes(Create(password))
            );
    }
}
