using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Cantina.Application;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Cantina.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth").RequireRateLimiting("auth").WithTags("Authentication");
        auth.MapPost("/register", RegisterAsync);
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/refresh", RefreshAsync);
        auth.MapGet("/me", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : Results.Ok(new
            {
                user.Id, user.Email, roles = await users.GetRolesAsync(user)
            });
        }).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(Credentials input, UserManager<ApplicationUser> users)
    {
        ValidateCredentials(input, registration: true);
        var email = input.Email.Trim();
        var user = new ApplicationUser { UserName = email, Email = email };
        var created = await users.CreateAsync(user, input.Password);
        if (created.Succeeded)
        {
            var role = await users.AddToRoleAsync(user, "Customer");
            if (!role.Succeeded)
            {
                await users.DeleteAsync(user);
                throw new InvalidOperationException("Could not provision customer role.");
            }
        }
        // An identical response for duplicate addresses and rejected passwords prevents account enumeration.
        return Results.Accepted(value: new { message = "Registration processed. If eligible, the account is ready to sign in." });
    }

    private static async Task<IResult> LoginAsync(Credentials input, SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users, HttpContext context)
    {
        ValidateCredentials(input, registration: false);
        signIn.AuthenticationScheme = IdentityConstants.BearerScheme;
        var user = await users.FindByEmailAsync(input.Email.Trim());
        if (user is null)
        {
            // Spend password-hashing work even when no account exists.
            users.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, input.Password);
            return ApiErrors.Problem(context, 401);
        }
        // This API does not expose a second-factor flow or issue temporary identity cookies.
        if (await users.GetTwoFactorEnabledAsync(user))
            return ApiErrors.Problem(context, 401);
        var result = await signIn.PasswordSignInAsync(user, input.Password, isPersistent: false, lockoutOnFailure: true);
        return result.Succeeded ? Results.Empty : ApiErrors.Problem(context, 401);
    }

    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Guid.NewGuid().ToString());

    private static async Task<IResult> RefreshAsync(RefreshInput input,
        IOptionsMonitor<BearerTokenOptions> options, SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(input.RefreshToken) || input.RefreshToken.Length > 16384)
            return ApiErrors.Problem(context, 401);
        var bearer = options.Get(IdentityConstants.BearerScheme);
        var ticket = bearer.RefreshTokenProtector.Unprotect(input.RefreshToken);
        if (ticket?.Properties.ExpiresUtc is not { } expiration || expiration <= DateTimeOffset.UtcNow)
            return ApiErrors.Problem(context, 401);
        var user = await signIn.ValidateSecurityStampAsync(ticket.Principal);
        if (user is null || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
            return ApiErrors.Problem(context, 401);
        var principal = await signIn.CreateUserPrincipalAsync(user);
        return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    public static async Task ValidateAccessTokenAsync(MessageReceivedContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return;
        var token = authorization["Bearer ".Length..].Trim();
        if (token.Length > 16384)
        {
            context.Fail("Invalid token.");
            return;
        }
        var ticket = context.Options.BearerTokenProtector.Unprotect(token);
        if (ticket?.Properties.ExpiresUtc is not { } expiration || expiration <= DateTimeOffset.UtcNow)
        {
            context.Fail("Invalid token.");
            return;
        }
        var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await signIn.ValidateSecurityStampAsync(ticket.Principal);
        if (user is null || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
            context.Fail("Invalid token.");
    }

    private static void ValidateCredentials(Credentials input, bool registration)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 254 ||
            !new EmailAddressAttribute().IsValid(input.Email.Trim()))
            errors["email"] = ["A valid email of at most 254 characters is required."];
        if (string.IsNullOrEmpty(input.Password) || input.Password.Length > 128 ||
            (registration && input.Password.Length < 12))
            errors["password"] = [registration
                ? "Password must contain between 12 and 128 characters."
                : "Password must contain between 1 and 128 characters."];
        if (errors.Count != 0) throw new RequestValidationException(errors);
    }

    public sealed record Credentials(string Email, string Password);
    public sealed record RefreshInput(string RefreshToken);
}
