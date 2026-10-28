using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Asp.Versioning.Builder;
using Cantina.Application;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Cantina.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app, ApiVersionSet versions)
    {
        var auth = app.MapGroup("/api/v{version:apiVersion}/auth").WithApiVersionSet(versions)
            .RequireRateLimiting("auth").WithTags("Authentication");
        auth.MapPost("/register", RegisterAsync);
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/refresh", RefreshAsync);
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization();
        auth.MapExternalAuthEndpoints();
        auth.MapGet("/me", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : Results.Ok(new
            {
                user.Id, user.Name, user.Email, roles = await users.GetRolesAsync(user)
            });
        }).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(Registration input, UserManager<ApplicationUser> users,
        CustomerProvisioner provisioner, ILoggerFactory loggers)
    {
        var name = input.Name?.Trim() ?? "";
        var email = input.Email?.Trim() ?? "";
        var user = new ApplicationUser { UserName = email, Email = email, Name = name };
        await ValidateAsync(input.Email, input.Password, name, users, user);
        var created = await provisioner.CreateCustomerAsync(user, input.Password, login: null);
        if (!created.Succeeded &&
            !created.Errors.All(error => error.Code is "DuplicateEmail" or "DuplicateUserName" or "DuplicateAccount"))
            loggers.CreateLogger(typeof(AuthEndpoints)).LogWarning(
                "Registration was rejected by Identity with codes {IdentityErrorCodes}",
                string.Join(",", created.Errors.Select(error => error.Code)));
        // An identical response for duplicate addresses and rejected passwords prevents account enumeration.
        return Results.Accepted(value: new { message = "Registration processed. If eligible, the account is ready to sign in." });
    }

    private static async Task<IResult> LoginAsync(Credentials input, SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users, HttpContext context)
    {
        await ValidateAsync(input.Email, input.Password, name: null, users, candidate: null);
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

    private static async Task<IResult> LogoutAsync(ClaimsPrincipal principal,
        UserManager<ApplicationUser> users, HttpContext context)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiErrors.Problem(context, 401);
        // Rotating the security stamp revokes every access and refresh token already issued to this account.
        return (await users.UpdateSecurityStampAsync(user)).Succeeded
            ? Results.NoContent() : ApiErrors.Problem(context, 500);
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

    private static async Task ValidateAsync(string? email, string? password, string? name,
        UserManager<ApplicationUser> users, ApplicationUser? candidate)
    {
        var errors = new Dictionary<string, string[]>();
        if (name is not null && name.Length is 0 or > 120)
            errors["name"] = ["A name of between 1 and 120 characters is required."];
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 ||
            !new EmailAddressAttribute().IsValid(email.Trim()))
            errors["email"] = ["A valid email of at most 254 characters is required."];
        if (string.IsNullOrEmpty(password) || password.Length > 128)
            errors["password"] = ["Password must contain between 1 and 128 characters."];
        else if (candidate is not null)
        {
            // Identity's configured PasswordOptions stay the single source of truth for strength rules.
            var failures = new List<string>();
            foreach (var validator in users.PasswordValidators)
            {
                var result = await validator.ValidateAsync(users, candidate, password);
                if (!result.Succeeded) failures.AddRange(result.Errors.Select(error => error.Description));
            }
            if (failures.Count != 0) errors["password"] = [.. failures];
        }
        if (errors.Count != 0) throw new RequestValidationException(errors);
    }

    public sealed record Registration(string Name, string Email, string Password);
    public sealed record Credentials(string Email, string Password);
    public sealed record RefreshInput(string RefreshToken);
}
