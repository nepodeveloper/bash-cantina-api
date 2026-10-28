using System.Threading.RateLimiting;
using Cantina.Api;
using Cantina.Application;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 64 * 1024);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = IdentityConstants.BearerScheme;
    options.DefaultChallengeScheme = IdentityConstants.BearerScheme;
    options.DefaultForbidScheme = IdentityConstants.BearerScheme;
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ManageMenu", policy => policy.RequireAuthenticatedUser().RequireRole("Admin", "StoreManager"));
    options.AddPolicy("RateMenu", policy => policy.RequireAuthenticatedUser().RequireRole("Customer"));
});
builder.Services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
{
    options.BearerTokenExpiration = TimeSpan.FromMinutes(15);
    options.RefreshTokenExpiration = TimeSpan.FromDays(7);
    options.Events.OnMessageReceived = AuthEndpoints.ValidateAccessTokenAsync;
});
var protection = builder.Services.AddDataProtection().SetApplicationName("BashCantina");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 128,
            QueueLimit = 0
        }));
    AddIpPolicy("auth", builder.Configuration.GetValue("Authentication:RateLimit:PermitLimit", 10));
    AddIpPolicy("catalogue", builder.Configuration.GetValue("Catalogue:RateLimit:PermitLimit", 100));
    options.OnRejected = async (context, ct) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 1;
        context.HttpContext.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ApiErrors.Problem(context.HttpContext, 429).ExecuteAsync(context.HttpContext);
    };

    void AddIpPolicy(string name, int limit)
    {
        if (limit is < 1 or > 100000)
            throw new InvalidOperationException("Rate limit permit counts must be between 1 and 100000.");
        options.AddPolicy(name, context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
    }
});

var app = builder.Build();
app.UseMiddleware<ApiErrors>();
app.UseStatusCodePages(async context =>
    await ApiErrors.Problem(context.HttpContext, context.HttpContext.Response.StatusCode)
        .ExecuteAsync(context.HttpContext));
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("Security:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi("/openapi/{documentName}.json").RequireRateLimiting("catalogue");

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", async (CantinaDbContext db, HttpContext context, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "healthy" })
            : ApiErrors.Problem(context, 503);
    }
    catch (Exception) when (!ct.IsCancellationRequested)
    {
        return ApiErrors.Problem(context, 503);
    }
});
app.MapAuthEndpoints();
app.MapCatalogueEndpoints();

if (app.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await SeedData.InitializeAsync(scope.ServiceProvider);
}
if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(app.Configuration["DataProtection:KeysPath"]))
    app.Logger.LogWarning("DataProtection:KeysPath is unset. Configure a persistent, shared key directory before deploying multiple instances.");
app.Run();

public partial class Program;
