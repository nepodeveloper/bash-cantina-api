using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Cantina.Api;

public sealed class ExternalProviderOptions
{
    public string DisplayName { get; set; } = "";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    public bool IsComplete => !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// The set of OpenID Connect providers this deployment enabled, plus the return URLs a login may
/// finish at. Adding a provider is configuration only; no code changes and no new schemes in code.
/// </summary>
public sealed class ExternalProviderRegistry(IReadOnlyDictionary<string, string> providers, IReadOnlyList<string> returnUrls)
{
    public bool IsEnabled(string scheme) => providers.ContainsKey(scheme);

    public IReadOnlyList<object> Describe() =>
        [.. providers.Select(provider => new
        {
            scheme = provider.Key,
            displayName = provider.Value,
            startUrl = $"/api/v1/auth/external/{provider.Key}/start"
        })];

    /// <summary>Exact-match allow list: an open redirect here would leak the login code to an attacker.</summary>
    public bool IsAllowedReturnUrl(string? returnUrl) =>
        returnUrl is not null && returnUrls.Contains(returnUrl, StringComparer.Ordinal);
}

/// <summary>
/// Short-lived, single-use codes exchanged for bearer tokens. Redirecting with a code instead of a
/// token keeps credentials out of browser history, proxy logs and the Referer header.
/// </summary>
public sealed class ExternalLoginCodes(IDataProtectionProvider protection, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly IDataProtector protector = protection.CreateProtector("Cantina.ExternalLogin.v2", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, long> consumed = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public string Issue(string userId, string securityStamp) => protector.Protect(string.Join('|',
        Guid.NewGuid().ToString("N"), userId, securityStamp, clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds()));

    public sealed record Login(string UserId, string SecurityStamp);

    public Login? TryConsume(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 4096) return null;
        string payload;
        try
        {
            payload = protector.Unprotect(code);
        }
        catch (CryptographicException)
        {
            return null;
        }
        var parts = payload.Split('|');
        if (parts.Length != 4 || !long.TryParse(parts[3], out var expiry)) return null;
        lock (gate)
        {
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (expiry <= now || consumed.ContainsKey(parts[0])) return null;
            foreach (var expired in consumed.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
                consumed.Remove(expired);
            if (consumed.Count >= 10_000) return null;
            consumed.Add(parts[0], expiry);
        }
        return new Login(parts[1], parts[2]);
    }
}

public static class ExternalAuthentication
{
    public static IServiceCollection AddExternalLogins(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:External");
        var returnUrls = section.GetSection("ReturnUrls").Get<string[]>() ?? [];
        var configured = section.GetSection("Providers").Get<Dictionary<string, ExternalProviderOptions>>() ?? [];
        var enabled = configured.Where(provider => provider.Value.IsComplete).ToDictionary(
            provider => provider.Key.ToLowerInvariant(),
            provider => string.IsNullOrWhiteSpace(provider.Value.DisplayName) ? provider.Key : provider.Value.DisplayName);

        if (enabled.Count != 0 && returnUrls.Length == 0)
            throw new InvalidOperationException(
                "Authentication:External:ReturnUrls must list at least one allow-listed return URL when providers are configured.");
        foreach (var url in returnUrls)
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                throw new InvalidOperationException($"Authentication:External:ReturnUrls contains an invalid absolute URL: {url}");

        services.AddSingleton(new ExternalProviderRegistry(enabled, returnUrls));
        services.AddSingleton<ExternalLoginCodes>();
        if (enabled.Count == 0) return services;

        var authentication = services.AddAuthentication();
        // Identity already registers the Identity.External cookie; it only carries the in-flight
        // handshake, while the resulting session stays bearer-token based like password login.
        foreach (var (scheme, _) in enabled)
        {
            var options = configured.First(provider =>
                string.Equals(provider.Key, scheme, StringComparison.OrdinalIgnoreCase)).Value;
            authentication.AddOpenIdConnect(scheme, enabled[scheme], handler =>
            {
                handler.Authority = options.Authority;
                handler.ClientId = options.ClientId;
                handler.ClientSecret = options.ClientSecret;
                handler.ResponseType = OpenIdConnectResponseType.Code;
                handler.UsePkce = true;
                handler.SaveTokens = false;
                handler.GetClaimsFromUserInfoEndpoint = true;
                handler.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
                handler.CallbackPath = $"/signin-{scheme}";
                handler.SignInScheme = IdentityConstants.ExternalScheme;
                handler.Scope.Clear();
                handler.Scope.Add("openid");
                handler.Scope.Add("profile");
                handler.Scope.Add("email");
                handler.MapInboundClaims = true;
            });
        }
        return services;
    }
}
