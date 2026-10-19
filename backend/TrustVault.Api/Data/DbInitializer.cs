using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Models;

namespace TrustVault.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider, IConfiguration configuration, ILogger logger)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        try
        {
            // Apply migrations for PostgreSQL or ensure schema is created for SQLite/InMemory
            if (context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await context.Database.MigrateAsync();
                logger.LogInformation("PostgreSQL database migrations applied successfully.");
            }
            else
            {
                await context.Database.EnsureCreatedAsync();
                logger.LogInformation("Database created via schema generation ({Provider}).", context.Database.ProviderName);
            }

            // Seed Roles
            foreach (var roleName in Roles.All)
            {
                if (!await roleManager.RoleExistsAsync(roleName))
                {
                    await roleManager.CreateAsync(new IdentityRole<Guid> { Name = roleName, NormalizedName = roleName.ToUpperInvariant() });
                    logger.LogInformation("Seeded role: {Role}", roleName);
                }
            }

            // Seed Default Admin User if configured
            var adminEmail = configuration["SeedData:AdminEmail"] ?? "admin@trustvault.local";
            var adminPassword = configuration["SeedData:AdminPassword"] ?? "Admin#TrustVault2026!";
            var existingAdmin = await userManager.FindByEmailAsync(adminEmail);

            if (existingAdmin == null)
            {
                var admin = new ApplicationUser
                {
                    UserName = adminEmail,
                    Email = adminEmail,
                    EmailConfirmed = true,
                    FullName = "TrustVault System Administrator",
                    CreatedAtUtc = DateTime.UtcNow,
                    IsActive = true
                };

                var createResult = await userManager.CreateAsync(admin, adminPassword);
                if (createResult.Succeeded)
                {
                    await userManager.AddToRoleAsync(admin, Roles.Admin);
                    logger.LogInformation("Default Admin user seeded: {Email}", adminEmail);
                }
                else
                {
                    var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                    logger.LogWarning("Failed to seed Admin user: {Errors}", errors);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during database migration/seeding.");
            throw;
        }
    }
}
