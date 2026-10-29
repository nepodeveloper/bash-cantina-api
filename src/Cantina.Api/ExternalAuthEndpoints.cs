using System.Security.Claims;
using Cantina.Application;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace Cantina.Api;

/// <summary>
/// OAuth 2.0 / OpenID Connect authorization-code sign-in. The provider handshake ends at a
/// one-time code, which the client exchanges for the same bearer tokens password login issues,
/// so the rest of the API has a single notion of a session.
/// </summary>
public static class ExternalAuthEndpoints
{
    public static void MapExternalAuthEndpoints(this RouteGroupBuilder auth)
    {
        var external = auth.MapGroup("/external").WithTags("External authentication");
        external.MapGet("/providers", (ExternalProviderRegistry registry) => Results.Ok(registry.Describe()));
        external.MapGet("/{provider}/start", Start);
        external.MapGet("/callback", CallbackAsync);
        external.MapPost("/exchange", ExchangeAsync);
    }

    private static IResult Start(string provider, string? returnUrl, ExternalProviderRegistry registry,
        SignInManager<ApplicationUser> signIn, HttpContext context)
    {
        if (!registry.IsEnabled(provider)) return ApiErrors.Problem(context, 404);
        if (!registry.IsAllowedReturnUrl(returnUrl))
            throw new RequestValidationException(new()
            {
                ["returnUrl"] = ["Supply a return URL that is on the configured allow list."]
            });
        var properties = signIn.ConfigureExternalAuthenticationProperties(provider, "/api/v1/auth/external/callback");
        properties.Items["returnUrl"] = returnUrl;
        return Results.Challenge(properties, [provider]);
    }

    private static async Task<IResult> CallbackAsync(SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users, CustomerProvisioner provisioner, ExternalProviderRegistry registry,
        ExternalLoginCodes codes, ILoggerFactory loggers, HttpContext context)
    {
        var logger = loggers.CreateLogger(typeof(ExternalAuthEndpoints));
        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null) return ApiErrors.Problem(context, 401);
        string? returnUrl = null;
        info.AuthenticationProperties?.Items.TryGetValue("returnUrl", out returnUrl);
        if (!registry.IsAllowedReturnUrl(returnUrl)) return ApiErrors.Problem(context, 400);

        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user is null)
        {
            var email = info.Principal.FindFirstValue(ClaimTypes.Email)?.Trim();
            // Linking on an unverified address would let anyone claim an existing account at that address.
            var verified = string.Equals(info.Principal.FindFirstValue("email_verified"), "true",
                StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(email) || !verified)
            {
                logger.LogWarning("External login from {Provider} supplied no verified email address", info.LoginProvider);
                return ApiErrors.Problem(context, 403, "The provider did not supply a verified email address.");
            }

            var login = new UserLoginInfo(info.LoginProvider, info.ProviderKey, info.ProviderDisplayName);
            user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    Name = Display(info, email)
                };
                var created = await provisioner.CreateCustomerAsync(user, password: null, login);
                if (!created.Succeeded) return ApiErrors.Problem(context, 409, "The account could not be created.");
                logger.LogInformation("Provisioned a customer from external provider {Provider}", info.LoginProvider);
            }
            else
            {
                var linked = await users.AddLoginAsync(user, login);
                if (!linked.Succeeded) return ApiErrors.Problem(context, 409, "The account could not be linked.");
                logger.LogInformation("Linked external provider {Provider} to an existing account", info.LoginProvider);
            }
        }

        if (!await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
            return ApiErrors.Problem(context, 401);
        await context.SignOutAsync(IdentityConstants.ExternalScheme);
        return Results.Redirect(QueryHelpers.AddQueryString(returnUrl!, "code",
            codes.Issue(user.Id, await users.GetSecurityStampAsync(user))));
    }

    private static async Task<IResult> ExchangeAsync(ExchangeInput input, ExternalLoginCodes codes,
        SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, HttpContext context)
    {
        if (codes.TryConsume(input.Code) is not { } login) return ApiErrors.Problem(context, 401);
        var user = await users.FindByIdAsync(login.UserId);
        if (user is null || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user)
            || await users.GetTwoFactorEnabledAsync(user)
            || !string.Equals(login.SecurityStamp, await users.GetSecurityStampAsync(user), StringComparison.Ordinal))
            return ApiErrors.Problem(context, 401);
        var principal = await signIn.CreateUserPrincipalAsync(user);
        return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    private static string Display(ExternalLoginInfo info, string email)
    {
        var claimed = info.Principal.FindFirstValue(ClaimTypes.Name)
            ?? info.Principal.FindFirstValue("name")
            ?? email.Split('@')[0];
        return claimed.Trim() is { Length: > 0 and <= 120 } name ? name : email.Split('@')[0][..Math.Min(120, email.Length)];
    }

    public sealed record ExchangeInput(string Code);
}
