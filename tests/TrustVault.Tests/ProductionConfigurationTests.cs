using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace TrustVault.Tests;

public class ProductionConfigurationTests
{
    [Fact]
    public void ParseDatabaseUrl_ValidPostgresUri_ConvertsToNpgsqlFormat()
    {
        // Arrange
        var uriString = "postgres://vault_user:p%40ssword123@dpg-abc123xyz.oregon-postgres.render.com:5432/trustvault_prod";

        // Act
        var uri = new Uri(uriString);
        var userInfo = uri.UserInfo.Split(':');
        var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "";
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');

        var npgsqlString = $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true;";

        // Assert
        npgsqlString.Should().Contain("Host=dpg-abc123xyz.oregon-postgres.render.com");
        npgsqlString.Should().Contain("Port=5432");
        npgsqlString.Should().Contain("Database=trustvault_prod");
        npgsqlString.Should().Contain("Username=vault_user");
        npgsqlString.Should().Contain("Password=p@ssword123");
        npgsqlString.Should().Contain("SSL Mode=Require");
    }

    [Fact]
    public void CorsOrigins_SplitsCommaAndSemicolonDelimitedOrigins()
    {
        // Arrange
        var defaultDevOrigins = new[] { "http://localhost:5173", "http://localhost:3000", "http://localhost:4173" };
        var configured = "https://trustvault.vercel.app, https://trustvault-preview-git.vercel.app; https://custom-domain.com";

        // Act
        var allowedOrigins = new HashSet<string>(defaultDevOrigins, StringComparer.OrdinalIgnoreCase);
        var customOrigins = configured.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var o in customOrigins)
        {
            if (Uri.TryCreate(o, UriKind.Absolute, out var u))
            {
                allowedOrigins.Add(u.GetLeftPart(UriPartial.Authority));
            }
            else
            {
                allowedOrigins.Add(o);
            }
        }

        // Assert
        allowedOrigins.Should().Contain("http://localhost:5173");
        allowedOrigins.Should().Contain("https://trustvault.vercel.app");
        allowedOrigins.Should().Contain("https://trustvault-preview-git.vercel.app");
        allowedOrigins.Should().Contain("https://custom-domain.com");
        allowedOrigins.Should().NotContain("*");
    }

    [Theory]
    [InlineData("TrustVaultSuperSecretKeyForDevelopment2026!MustBeAtLeast32BytesLongForHmacSha256", true)]
    [InlineData("TrustVaultSuperSecretKeyForDevelopment2026!MustBeAtLeast32Bytes", true)]
    [InlineData("too-short-key", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("ProductionSafeSuperSecretKeyWithSufficientLengthAndEntropy2026!", false)]
    public void ProductionJwtKeyGuard_RejectsInsecureKeys(string? key, bool shouldReject)
    {
        const string devKey1 = "TrustVaultSuperSecretKeyForDevelopment2026!MustBeAtLeast32BytesLongForHmacSha256";
        const string devKey2 = "TrustVaultSuperSecretKeyForDevelopment2026!MustBeAtLeast32Bytes";

        var isInvalid = string.IsNullOrWhiteSpace(key) ||
                        key == devKey1 ||
                        key == devKey2 ||
                        key.Length < 32;

        isInvalid.Should().Be(shouldReject);
    }

    [Theory]
    [InlineData("Admin#TrustVault2026!", true)]
    [InlineData("CustomProdAdmin#SecurePass2026!", false)]
    public void ProductionAdminPasswordGuard_RejectsDefaultSeedPassword(string password, bool shouldReject)
    {
        var isDefault = password == "Admin#TrustVault2026!";
        isDefault.Should().Be(shouldReject);
    }
}
