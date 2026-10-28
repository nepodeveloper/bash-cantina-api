using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cantina.Infrastructure;

public static class SeedData
{
    public static readonly Guid BashStoreId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SportsceneStoreId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Seeded rows need a constant timestamp so the migration snapshot stays reproducible.
    public static readonly DateTimeOffset SeedTimestamp = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var configuration = provider.GetRequiredService<IConfiguration>();
        ValidateBootstrapConfiguration(configuration);
        var email = configuration["BootstrapAdmin:Email"];
        var password = configuration["BootstrapAdmin:Password"];

        var db = provider.GetRequiredService<CantinaDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        var roles = provider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Customer", "StoreManager", "Admin" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await roles.RoleExistsAsync(role))
                EnsureSucceeded(await roles.CreateAsync(new IdentityRole(role)), $"creating role {role}");
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByEmailAsync(email) is null && await users.FindByNameAsync(email) is null)
                // The retrying execution strategy owns the transaction so a transient failure replays the whole unit.
                await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, Name = "Bootstrap administrator" };
                    EnsureSucceeded(await users.CreateAsync(user, password!), "creating bootstrap administrator");
                    EnsureSucceeded(await users.AddToRoleAsync(user, "Admin"), "assigning bootstrap administrator role");
                    await transaction.CommitAsync(cancellationToken);
                });
        }
    }

    internal static void ValidateBootstrapConfiguration(IConfiguration configuration)
    {
        var hasEmail = !string.IsNullOrWhiteSpace(configuration["BootstrapAdmin:Email"]);
        var hasPassword = !string.IsNullOrWhiteSpace(configuration["BootstrapAdmin:Password"]);
        if (hasEmail != hasPassword)
            throw new InvalidOperationException("BootstrapAdmin:Email and BootstrapAdmin:Password must both be configured.");
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Failed {operation}: {string.Join("; ", result.Errors.Select(x => x.Description))}");
    }
}
