using Cantina.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cantina.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "PostgreSQL";
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        SeedData.ValidateBootstrapConfiguration(configuration);

        services.TryAddSingleton(TimeProvider.System);
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            connectionString ??= "Data Source=cantina.db";
            services.AddDbContext<CantinaDbContext>(options => options.UseSqlite(connectionString));
        }
        else if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required for PostgreSQL.");
            services.AddDbContext<CantinaDbContext>(options => options.UseNpgsql(connectionString,
                // Transient network and failover errors are retried in place before the caller ever sees them.
                npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)));
        }
        else
        {
            throw new InvalidOperationException($"Unsupported database provider '{provider}'. Use Sqlite or PostgreSQL.");
        }
        services.AddIdentityApiEndpoints<ApplicationUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireDigit = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.User.RequireUniqueEmail = true;
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<CantinaDbContext>()
            .AddUserStore<ApplicationUserStore>();
        services.AddHealthChecks().AddDbContextCheck<CantinaDbContext>("database", tags: ["ready"]);
        services.AddSingleton<CatalogueCache>();
        services.AddScoped<CustomerProvisioner>();
        services.AddScoped<IMenuRepository, MenuRepository>();
        services.AddScoped<IReviewDashboard, ReviewDashboard>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<MenuService>();
        services.AddScoped<ReviewService>();
        return services;
    }
}

public sealed class CantinaDbContextFactory : IDesignTimeDbContextFactory<CantinaDbContext>
{
    public CantinaDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = "Host=localhost;Database=cantina_design;Username=cantina_design";
        return new(new DbContextOptionsBuilder<CantinaDbContext>().UseNpgsql(connectionString).Options);
    }
}
