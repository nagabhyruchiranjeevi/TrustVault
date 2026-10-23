using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
    public void ParseDatabaseUrl_PasswordWithHash_ParsesCorrectlyWithoutThrowing()
    {
        // Arrange — password contains an unescaped '#' which breaks standard RFC 3986 URI parsers (treated as fragment)
        var url = "postgresql://postgres.glstabcxyz:Synthetic#Pass2026!@aws-0-us-east-1.pooler.supabase.com:5432/postgres";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("aws-0-us-east-1.pooler.supabase.com");
        parsed.Port.Should().Be(5432);
        parsed.Database.Should().Be("postgres");
        parsed.Username.Should().Be("postgres.glstabcxyz");
        parsed.Password.Should().Be("Synthetic#Pass2026!");
        parsed.SslMode.Should().Be(SslMode.Require);
    }


    [Fact]
    public void ParseDatabaseUrl_PasswordWithUnencodedAtSymbol_ParsesCorrectly()
    {
        // Arrange — password contains an unescaped '@' (before host separator '@')
        var url = "postgresql://vault_user:p@ssword123@aws-0-us-east-1.pooler.supabase.com:5432/trustvault_prod";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("aws-0-us-east-1.pooler.supabase.com");
        parsed.Username.Should().Be("vault_user");
        parsed.Password.Should().Be("p@ssword123");
    }

    [Fact]
    public void ParseDatabaseUrl_PasswordWithBackslash_ParsesCorrectly()
    {
        // Arrange — password contains backslash
        var url = "postgresql://vault_user:pass\\word@host.example.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("host.example.com");
        parsed.Username.Should().Be("vault_user");
        parsed.Password.Should().Be("pass\\word");
    }

    [Fact]
    public void ParseDatabaseUrl_IPv6HostInBrackets_ParsesCorrectly()
    {
        // Arrange — IPv6 address in brackets
        var url = "postgresql://vault_user:secret@[2001:db8::1]:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("[2001:db8::1]");
        parsed.Port.Should().Be(5432);
        parsed.Username.Should().Be("vault_user");
        parsed.Password.Should().Be("secret");
    }

    [Fact]
    public void ParseDatabaseUrl_IPv6HostWithHashInPassword_ParsesCorrectly()
    {
        // Arrange — IPv6 address in brackets with '#' in password (forcing direct parser)
        var url = "postgresql://vault_user:secret#pass@[2001:db8::1]:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("[2001:db8::1]");
        parsed.Port.Should().Be(5432);
        parsed.Username.Should().Be("vault_user");
        parsed.Password.Should().Be("secret#pass");
    }

    [Fact]
    public void ParseDatabaseUrl_PasswordWithQuestionMarkAndHashAndQuery_PreservesAllAndParsesQueryCorrectly()
    {
        // Arrange — password contains '?' and '#' while the URL also has query parameters
        var url = "postgresql://vault_user:p?ss#w?rd@aws-0-us-east-1.pooler.supabase.com:5432/trustvault_prod?sslmode=require&pooling=true";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — query params must not be confused with '?' in password
        parsed.Host.Should().Be("aws-0-us-east-1.pooler.supabase.com");
        parsed.Database.Should().Be("trustvault_prod");
        parsed.Username.Should().Be("vault_user");
        parsed.Password.Should().Be("p?ss#w?rd");
        parsed.SslMode.Should().Be(SslMode.Require);
        parsed.Pooling.Should().BeTrue();
    }

    [Fact]
    public void ParseDatabaseUrl_PasswordWithCombinationOfSpecialCharacters_PreservesAll()
    {
        // Arrange — password with unencoded @, :, ;, #, ?, and backslash
        var url = "postgresql://vault_user:p@ss:w;rd#1?2\\3@db.example.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("db.example.com");
        parsed.Username.Should().Be("vault_user");
        parsed.Password.Should().Be("p@ss:w;rd#1?2\\3");
    }

    [Theory]
    [InlineData("postgresql://vault_user:secret@host.example.com:invalid/mydb")]
    [InlineData("postgresql://vault_user:secret@host.example.com:0/mydb")]
    [InlineData("postgresql://vault_user:secret@host.example.com:70000/mydb")]
    [InlineData("postgresql://vault_user:secret@host.example.com:-5/mydb")]
    public void ParseDatabaseUrl_InvalidPort_ThrowsArgumentException(string url)
    {
        // Act
        var act = () => Program.ParseDatabaseUrlToNpgsql(url);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*port*");
    }

    [Fact]
    public void ParseDatabaseUrl_MalformedPercentEscapeInDatabase_ThrowsArgumentException()
    {
        // Arrange — invalid percent escape in database name
        var url = "postgresql://vault_user:secret@host.example.com:5432/my%ZZdb";

        // Act
        var act = () => Program.ParseDatabaseUrlToNpgsql(url);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*percent escape*");
    }

    [Fact]
    public void ParseDatabaseUrl_Production_SslModeDisable_ThrowsSecurityException()
    {
        // Arrange — production connection string attempting to disable SSL
        var url = "postgresql://vault_user:secret@aws-0-us-east-1.pooler.supabase.com:5432/mydb?sslmode=disable";

        // Act
        var act = () => Program.ParseDatabaseUrlToNpgsql(url, isProduction: true);

        // Assert — in production, sslmode=disable must be blocked
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Insecure SSL mode*");
    }

    [Theory]
    [InlineData("postgresql://vault_user:secret@host.example.com:5432/mydb?sslmode=invalid")]
    [InlineData("postgresql://vault_user:secret@host.example.com:5432/mydb?sslmode=")]
    [InlineData("postgresql://vault_user:secret@host.example.com:5432/mydb?sslmode")]
    public void ParseDatabaseUrl_MalformedSslMode_ThrowsArgumentException(string url)
    {
        // Act
        var act = () => Program.ParseDatabaseUrlToNpgsql(url);

        // Assert — malformed SSL modes must fail closed
        act.Should().Throw<ArgumentException>()
            .WithMessage("*sslmode*");
    }

    [Fact]
    public void ParseDatabaseUrl_Production_OmittedSslMode_EnforcesSslRequire()
    {
        // Arrange — production URI with no sslmode specified
        var url = "postgresql://vault_user:secret@aws-0-us-east-1.pooler.supabase.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url, isProduction: true);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — production must enforce Require even when omitted from URI
        parsed.SslMode.Should().Be(SslMode.Require);
    }

    [Fact]
    public void ParseDatabaseUrl_Ipv4Host_ParsesCorrectly()
    {
        // Arrange — IPv4 host address
        var url = "postgresql://vault_user:secret@192.168.1.100:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("192.168.1.100");
        parsed.Port.Should().Be(5432);
        parsed.Username.Should().Be("vault_user");
        parsed.Password.Should().Be("secret");
    }

    [Fact]
    public void ParseDatabaseUrl_EmptyCredentials_ProducesValidConnectionStringWithoutUsernameOrPassword()
    {
        // Arrange — empty credentials authority (e.g. peer / IAM auth)
        var url = "postgresql://@host.example.com:5432/mydb";

        // Act
        var connStr = Program.ParseDatabaseUrlToNpgsql(url);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        parsed.Host.Should().Be("host.example.com");
        parsed.Database.Should().Be("mydb");
        parsed.Username.Should().BeNullOrEmpty();
        parsed.Password.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ResolveDatabaseConfiguration_DatabaseUrl_WithHashInPassword_ResolvesSuccessfully()
    {
        // Arrange — Render production scenario where DATABASE_URL contains unencoded '#' in password
        var rawUrl = "postgresql://postgres.glstabcxyz:Synthetic#Pass2026!@aws-0-us-east-1.pooler.supabase.com:5432/postgres";
        var config = new ConfigurationBuilder().Build();

        // Act
        var (provider, connStr) = Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: rawUrl, isProduction: true);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        provider.Should().Be("PostgreSQL");
        parsed.Host.Should().Be("aws-0-us-east-1.pooler.supabase.com");
        parsed.Username.Should().Be("postgres.glstabcxyz");
        parsed.Password.Should().Be("Synthetic#Pass2026!");
        parsed.SslMode.Should().Be(SslMode.Require);
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

    // ──────────────────────────────────────────────────────────
    //  ResolveDatabaseConfiguration Regression Tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ResolveDatabaseConfiguration_ConnectionStringsDefaultConnection_SupabaseUri_IsNormalizedToNpgsqlFormat()
    {
        // Arrange — simulates Render deployment setting ConnectionStrings__DefaultConnection to a Supabase URI
        var inMemory = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "PostgreSQL",
            ["ConnectionStrings:DefaultConnection"] = "postgres://postgres.glstabcxyz:TestPass2026@aws-0-us-east-1.pooler.supabase.com:5432/postgres"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var (provider, connStr) = Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: true);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — provider must be PostgreSQL and connStr must NOT be the raw URI; it must be valid Npgsql key-value
        provider.Should().Be("PostgreSQL");
        parsed.Host.Should().Be("aws-0-us-east-1.pooler.supabase.com");
        parsed.Port.Should().Be(5432);
        parsed.Database.Should().Be("postgres");
        parsed.Username.Should().Be("postgres.glstabcxyz");
        parsed.SslMode.Should().Be(SslMode.Require);
    }

    [Fact]
    public void ResolveDatabaseConfiguration_DatabaseUrl_WithEnclosingDoubleQuotes_StripsQuotesAndNormalizes()
    {
        // Arrange — raw DATABASE_URL surrounded by double quotes (e.g. from Render or .env)
        var rawUrl = "\"postgres://vault_user:secret123@db.example.com:5432/trustvault\"";
        var config = new ConfigurationBuilder().Build();

        // Act
        var (provider, connStr) = Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: rawUrl, isProduction: true);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        provider.Should().Be("PostgreSQL");
        parsed.Host.Should().Be("db.example.com");
        parsed.Username.Should().Be("vault_user");
        parsed.Database.Should().Be("trustvault");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_DatabaseUrl_WithEnclosingSingleQuotes_StripsQuotesAndNormalizes()
    {
        // Arrange — raw DATABASE_URL surrounded by single quotes
        var rawUrl = "'postgres://vault_user:secret123@db.example.com:5432/trustvault'";
        var config = new ConfigurationBuilder().Build();

        // Act
        var (provider, connStr) = Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: rawUrl, isProduction: true);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        provider.Should().Be("PostgreSQL");
        parsed.Host.Should().Be("db.example.com");
        parsed.Username.Should().Be("vault_user");
        parsed.Database.Should().Be("trustvault");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_PrioritizesRawDatabaseUrlOverConfigDefaultConnection()
    {
        // Arrange — both DATABASE_URL parameter and ConnectionStrings:DefaultConnection exist
        var inMemory = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=secondary.example.com;Port=5432;Database=db2;Username=u2;Password=p2;"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var rawUrl = "postgres://primary_user:primary_pass@primary.example.com:5432/primary_db";

        // Act
        var (provider, connStr) = Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: rawUrl, isProduction: true);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert — DATABASE_URL must win
        provider.Should().Be("PostgreSQL");
        parsed.Host.Should().Be("primary.example.com");
        parsed.Username.Should().Be("primary_user");
        parsed.Database.Should().Be("primary_db");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_StandardHostConnectionString_PassedThroughUnchanged()
    {
        // Arrange — standard Npgsql key-value connection string in ConnectionStrings:DefaultConnection
        var standardConn = "Host=myhost.example.com;Port=5432;Database=mydb;Username=myuser;Password=mypass;";
        var inMemory = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "PostgreSQL",
            ["ConnectionStrings:DefaultConnection"] = standardConn
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var (provider, connStr) = Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: true);
        var parsed = new NpgsqlConnectionStringBuilder(connStr);

        // Assert
        provider.Should().Be("PostgreSQL");
        parsed.Host.Should().Be("myhost.example.com");
        parsed.Username.Should().Be("myuser");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_InfersPostgreSqlWhenCandidateExistsWithoutExplicitProvider()
    {
        // Arrange — DatabaseProvider not specified, but postgresql:// URI present in DefaultConnection
        var inMemory = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "postgresql://inferred_user:pass@host.example.com:5432/inferred_db"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var (provider, connStr) = Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: false);

        // Assert
        provider.Should().Be("PostgreSQL");
        connStr.Should().Contain("Host=host.example.com");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_DefaultsToSqliteWhenNoPostgreSqlCandidateExists()
    {
        // Arrange — empty config
        var inMemory = new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqliteConnection"] = "Data Source=custom.db"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var (provider, connStr) = Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: false);

        // Assert
        provider.Should().Be("Sqlite");
        connStr.Should().Be("Data Source=custom.db");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_Production_MissingPostgreSqlConnectionString_ThrowsSanitizedException()
    {
        // Arrange — Production with DatabaseProvider=PostgreSQL but no connection strings
        var inMemory = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "PostgreSQL"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var act = () => Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: true);

        // Assert — fails fast in production rather than silently using dev localhost fallback
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PostgreSQL*production*");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_InvalidPostgreSqlConnectionString_ThrowsSanitizedException()
    {
        // Arrange — non-URI and non-key-value garbage string
        var inMemory = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "PostgreSQL",
            ["ConnectionStrings:DefaultConnection"] = "some-invalid-format-string"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var act = () => Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: false);

        // Assert — fails with sanitized error, no secrets leaked
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CRITICAL DATABASE CONFIGURATION ERROR*");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_Production_DevDefaultConnection_ThrowsSanitizedException()
    {
        // Arrange — Production with DatabaseProvider=PostgreSQL and DefaultConnection having dev localhost fallback
        var inMemory = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "PostgreSQL",
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Port=5432;Database=trustvault_db;Username=postgres;Password=postgres;"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var act = () => Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: true);

        // Assert — must reject the localhost dev string in production and throw
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PostgreSQL*production*");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_Production_IpLoopback_ThrowsSanitizedException()
    {
        // Arrange — Production with 127.0.0.1 loopback host (not matching exact 'localhost' string)
        var inMemory = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "PostgreSQL",
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=5432;Database=proddb;Username=produser;Password=prodpass;"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var act = () => Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: true);

        // Assert — must reject 127.0.0.1 loopback in production
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*development or localhost*");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_Production_DevCredentialsOnRemoteHost_ThrowsSanitizedException()
    {
        // Arrange — Production with remote host but development default credentials (postgres:postgres)
        var inMemory = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "PostgreSQL",
            ["ConnectionStrings:DefaultConnection"] = "Host=aws-0-us-east-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres;Password=postgres;"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var act = () => Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: true);

        // Assert — must reject default dev credentials in production
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*development or localhost*");
    }

    [Fact]
    public void ResolveDatabaseConfiguration_Production_ExplicitSqlite_ThrowsSanitizedException()
    {
        // Arrange — Production with explicit DatabaseProvider=Sqlite
        var inMemory = new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "Sqlite"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        var act = () => Program.ResolveDatabaseConfiguration(config, rawDatabaseUrl: null, isProduction: true);

        // Assert — SQLite is not permitted in production
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SQLite is not permitted in production*");
    }
}
