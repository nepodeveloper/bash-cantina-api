using Cantina.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cantina.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
        SeedData.ValidateBootstrapConfiguration(configuration);

        services.AddDbContext<CantinaDbContext>(options => options.UseNpgsql(connectionString));
        services.AddIdentityApiEndpoints<ApplicationUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.User.RequireUniqueEmail = true;
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<CantinaDbContext>()
            .AddUserStore<ApplicationUserStore>();
        services.AddSingleton<CatalogueCache>();
        services.AddScoped<IMenuRepository, MenuRepository>();
        services.AddScoped<MenuService>();
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
