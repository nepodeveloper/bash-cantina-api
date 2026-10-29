using System.Security.Claims;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Cantina.Api;
using Cantina.Application;
using Cantina.Infrastructure;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

var initializeDatabase = args.Contains("--initialize-database", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--initialize-database").ToArray());
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 64 * 1024);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
    options.ReportApiVersions = true;
    options.UnsupportedApiVersionStatusCode = StatusCodes.Status404NotFound;
});
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "Bash Cantina API",
            Version = "v1",
            Description = "Store-scoped menu catalogue and customer ratings. Authentication uses opaque ASP.NET Core Identity bearer tokens, not JWTs."
        };
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "Identity opaque token",
            Description = "Use accessToken from /api/v1/auth/login or /api/v1/auth/refresh."
        };
        // Publish concrete v1 paths, and declare the slug that handlers resolve through StoreContext.
        var resolved = new OpenApiPaths();
        foreach (var path in document.Paths)
        {
            var route = path.Key.Replace("v{version}", "v1", StringComparison.Ordinal);
            if (route.Contains("{storeSlug}", StringComparison.Ordinal) && path.Value.Operations is { } operations)
                foreach (var operation in operations.Values)
                {
                    operation.Parameters ??= [];
                    if (!operation.Parameters.Any(parameter => parameter.Name == "storeSlug"))
                        operation.Parameters.Insert(0, new OpenApiParameter
                        {
                            Name = "storeSlug",
                            In = ParameterLocation.Path,
                            Required = true,
                            Description = "Store slug, for example bash or sportscene.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                        });
                }
            resolved[route] = path.Value;
        }
        document.Paths = resolved;
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, ct) =>
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any())
            operation.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] }
            ];
        return Task.CompletedTask;
    });
});
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<StoreContext>();
builder.Services.AddExternalLogins(builder.Configuration);
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

builder.Services.AddSingleton(_ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
{
    PermitLimit = 128,
    QueueLimit = 0
}));
builder.Services.AddRateLimiter(options =>
{
    AddFixedWindowPolicy("auth", builder.Configuration.GetValue("Authentication:RateLimit:PermitLimit", 10),
        context => "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
    // Catalogue routes authenticate first, so abuse is attributed to the account rather than a shared NAT address.
    AddFixedWindowPolicy("catalogue", builder.Configuration.GetValue("Catalogue:RateLimit:PermitLimit", 100),
        context => context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? "user:" + userId
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
    options.OnRejected = async (context, ct) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 1;
        context.HttpContext.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ApiErrors.Problem(context.HttpContext, 429).ExecuteAsync(context.HttpContext);
    };

    void AddFixedWindowPolicy(string name, int limit, Func<HttpContext, string> partitionKey)
    {
        if (limit is < 1 or > 100000)
            throw new InvalidOperationException("Rate limit permit counts must be between 1 and 100000.");
        options.AddPolicy(name, context =>
            RateLimitPartition.GetFixedWindowLimiter(partitionKey(context),
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
if (initializeDatabase)
{
    await using (var scope = app.Services.CreateAsyncScope())
        await SeedData.InitializeAsync(scope.ServiceProvider);
    await app.DisposeAsync();
    return;
}
app.UseMiddleware<ApiErrors>();
app.UseStatusCodePages(async context =>
    await ApiErrors.Problem(context.HttpContext, context.HttpContext.Response.StatusCode)
        .ExecuteAsync(context.HttpContext));
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("Security:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<GlobalConcurrencyMiddleware>();
app.UseAuthentication();
// Placed after authentication so catalogue limits can partition on the signed-in account.
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json").RequireRateLimiting("catalogue");
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Bash Cantina API v1");
        options.RoutePrefix = "swagger";
    });
}

// Probes stay outside the limiter so an overloaded instance is still reported accurately to orchestrators.
app.MapHealthChecks("/health/live",
    new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteHealthAsync }).DisableRateLimiting();
app.MapHealthChecks("/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready"), ResponseWriter = WriteHealthAsync })
    .DisableRateLimiting();
var versions = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).ReportApiVersions().Build();
app.MapAuthEndpoints(versions);
var storeScope = app.MapStoreEndpoints(versions);
storeScope.MapCatalogueEndpoints();
storeScope.MapReviewEndpoints();

if (app.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await SeedData.InitializeAsync(scope.ServiceProvider);
}
if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(app.Configuration["DataProtection:KeysPath"]))
    app.Logger.LogWarning("DataProtection:KeysPath is unset. Configure a persistent, shared key directory before deploying multiple instances.");
app.Run();

static Task WriteHealthAsync(HttpContext context, HealthReport report) =>
    report.Status == HealthStatus.Healthy
        ? context.Response.WriteAsJsonAsync(new { status = "healthy" })
        : ApiErrors.Problem(context, StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);

public partial class Program;
