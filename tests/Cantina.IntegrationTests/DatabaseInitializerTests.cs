using System.Diagnostics;
using Cantina.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Cantina.IntegrationTests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task Initializer_applies_migrations_and_roles_with_startup_migrations_disabled_and_exits_successfully()
    {
        await using var database = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await database.StartAsync();
        var assembly = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--initialize-database");
        start.Environment["ConnectionStrings__DefaultConnection"] = database.GetConnectionString();
        start.Environment["Database__ApplyMigrations"] = "false";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0,
                    $"Initializer exited {process.ExitCode}: {await output}\n{await error}");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }

        await using var db = new CantinaDbContext(new DbContextOptionsBuilder<CantinaDbContext>()
            .UseNpgsql(database.GetConnectionString()).Options);
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(new[] { "Admin", "Customer", "StoreManager" },
            await db.Roles.OrderBy(role => role.Name).Select(role => role.Name).ToArrayAsync());
        Assert.Equal(2, await db.Stores.CountAsync());
        Assert.Equal(10, await db.MenuItems.CountAsync());
    }
}
