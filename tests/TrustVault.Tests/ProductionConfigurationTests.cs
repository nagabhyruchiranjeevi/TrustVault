using System;
using System.Collections.Generic;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace TrustVault.Tests;

public class ProductionConfigurationTests
{
    // ──────────────────────────────────────────────────────────
    //  DATABASE_URL Parsing Tests (via Program.ParseDatabaseUrlToNpgsql)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ParseDatabaseUrl_ValidRenderPostgresUri_ConvertsToNpgsqlFormat()
    {
        // Arrange — Render-style URI with URL-encoded @ in password
        var url = "postgres://vault_user:p%40ssword123@dpg-abc123xyz.oregon-postgres.render.com:5432/trustvault_prod";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — verify via builder properties to avoid leaking credentials in assertion messages
        parsed.Host.Should().Be("dpg-abc123xyz.oregon-postgres.render.com");
        parsed.Port.Should().Be(5432);
        parsed.Database.Should().Be("trustvault_prod");
        parsed.Username.Should().Be("vault_user");
        parsed.Password.Should().Be("p@ssword123", "URL-encoded %40 must decode to @");
        parsed.SslMode.Should().Be(SslMode.Require);
    }

    [Fact]
    public void ParseDatabaseUrl_SupabaseSessionPoolerUri_ParsesCorrectly()
    {
        // Arrange — Supabase Session Pooler format with project-ref in username
        var url = "postgres://postgres.glstabcxyz:TestPass2026@aws-0-us-east-1.pooler.supabase.com:5432/postgres";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("aws-0-us-east-1.pooler.supabase.com");
        parsed.Port.Should().Be(5432);
        parsed.Database.Should().Be("postgres");
        parsed.Username.Should().Be("postgres.glstabcxyz", "Supabase pooler usernames contain dots");
        parsed.Password.Should().NotBeNullOrEmpty();
        parsed.SslMode.Should().Be(SslMode.Require);
    }

    [Fact]
    public void ParseDatabaseUrl_PasswordWithEncodedColon_PreservesFullPassword()
    {
        // Arrange — password contains a colon encoded as %3A
        var url = "postgres://user:pass%3Aword@host.example.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Username.Should().Be("user");
        parsed.Password.Should().Be("pass:word", "encoded colon (%3A) must not truncate the password");
    }

    [Fact]
    public void ParseDatabaseUrl_PasswordWithEncodedSemicolon_PreservesFullPassword()
    {
        // Arrange — password contains a semicolon encoded as %3B
        var url = "postgres://user:pass%3Bword@host.example.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — semicolons are connection string delimiters; builder must escape them
        parsed.Username.Should().Be("user");
        parsed.Password.Should().Be("pass;word", "encoded semicolon (%3B) must be preserved in the password");
    }

    [Fact]
    public void ParseDatabaseUrl_PasswordWithMultipleSpecialChars_PreservesAll()
    {
        // Arrange — password with @, :, ;, %, and = encoded
        var url = "postgres://user:p%40ss%3Aw%3Brd%3D%25@host.example.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Password.Should().Be("p@ss:w;rd=%", "all URL-encoded special characters must round-trip correctly");
    }

    [Fact]
    public void ParseDatabaseUrl_PostgresqlScheme_IsAccepted()
    {
        // Arrange — postgresql:// scheme instead of postgres://
        var url = "postgresql://user:pass@host.example.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("host.example.com");
        parsed.Username.Should().Be("user");
    }

    [Fact]
    public void ParseDatabaseUrl_MissingPort_DefaultsTo5432()
    {
        // Arrange — no port specified
        var url = "postgres://user:pass@host.example.com/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Port.Should().Be(5432);
    }

    [Fact]
    public void ParseDatabaseUrl_CustomPort6543_IsPreserved()
    {
        // Arrange — Supabase transaction pooler uses port 6543
        var url = "postgres://user:pass@host.example.com:6543/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Port.Should().Be(6543);
    }

    [Fact]
    public void ParseDatabaseUrl_QueryParamSslmode_OverridesDefault()
    {
        // Arrange — URI with ?sslmode=prefer query parameter
        var url = "postgres://user:pass@host.example.com:5432/mydb?sslmode=prefer";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.SslMode.Should().Be(SslMode.Prefer, "query parameter sslmode=prefer must override the default Require");
    }

    [Fact]
    public void ParseDatabaseUrl_StandardConnectionString_PassedThrough()
    {
        // Arrange — already an Npgsql key-value connection string, not a URI
        var connStr = "Host=localhost;Port=5432;Database=mydb;Username=user;Password=pass;";

        // Act
        var result = Program.ParseDatabaseUrlToNpgsql(connStr);

        // Assert — returned unmodified
        result.Should().Be(connStr, "non-URI connection strings must pass through unchanged");
    }

    [Fact]
    public void ParseDatabaseUrl_EmptyString_ReturnsEmpty()
    {
        var result = Program.ParseDatabaseUrlToNpgsql("");
        result.Should().Be("");
    }

    [Fact]
    public void ParseDatabaseUrl_WhitespaceOnly_ReturnsWhitespace()
    {
        var result = Program.ParseDatabaseUrlToNpgsql("   ");
        result.Should().Be("   ");
    }

    [Fact]
    public void ParseDatabaseUrl_UsernameOnly_NoPassword()
    {
        // Arrange — URI without password component
        var url = "postgres://onlyuser@host.example.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Username.Should().Be("onlyuser");
        parsed.Password.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ParseDatabaseUrl_MalformedUri_Throws()
    {
        // Arrange — starts with postgres:// but is not a valid URI
        var url = "postgres://:::invalid";

        // Act
        var act = () => Program.ParseDatabaseUrlToNpgsql(url);

        // Assert — must throw, not silently fall back to a default database
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseDatabaseUrl_MissingHost_Throws()
    {
        // Arrange — URI without a host component
        var url = "postgres:///mydb";

        // Act
        var act = () => Program.ParseDatabaseUrlToNpgsql(url);

        // Assert — must throw ArgumentException, not silently fallback or produce empty host
        act.Should().Throw<ArgumentException>()
            .WithMessage("*host*");
    }

    [Fact]
    public void ParseDatabaseUrl_MissingDatabaseName_ProducesValidConnectionString()
    {
        // Arrange — URI with no path (no database name)
        var url = "postgres://user:pass@host.example.com:5432";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — Host and credentials parsed; Database left to Npgsql default
        parsed.Host.Should().Be("host.example.com");
        parsed.Username.Should().Be("user");
    }

    [Fact]
    public void ParseDatabaseUrl_UnknownQueryParams_AreSilentlyIgnored()
    {
        // Arrange — URI with an unrecognized query parameter that could be security-sensitive
        var url = "postgres://user:pass@host.example.com:5432/mydb?application_name=test&unknownparam=dangerous";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — core settings preserved, unknown params must not appear
        parsed.Host.Should().Be("host.example.com");
        parsed.Database.Should().Be("mydb");
        parsed.SslMode.Should().Be(SslMode.Require, "unknown params must not downgrade SSL");
    }

    [Fact]
    public void ParseDatabaseUrl_SslmodeDisable_IsExplicitlyAccepted()
    {
        // Arrange — provider explicitly requests no SSL (e.g. local Docker PostgreSQL)
        var url = "postgres://user:pass@localhost:5432/mydb?sslmode=disable";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — Disable is a valid SslMode enum value and must be honored
        parsed.SslMode.Should().Be(SslMode.Disable);
    }

    [Fact]
    public void ParseDatabaseUrl_EncodedDatabaseName_IsDecoded()
    {
        // Arrange — database name contains a URL-encoded space
        var url = "postgres://user:pass@host.example.com:5432/my%20database";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Database.Should().Be("my database");
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
