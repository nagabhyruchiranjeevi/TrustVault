using System.Text;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using TrustVault.Api.Auth;
using TrustVault.Api.Data;
using TrustVault.Api.Middleware;
using TrustVault.Api.Models;
using TrustVault.Api.Services;
using TrustVault.Api.Validators;

var builder = WebApplication.CreateBuilder(args);

// 0. Bind to PORT environment variable if provided (e.g. Render, Railway, cloud container hosting)
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://+:{port}");
}

// 1. Database & EF Core setup (Supports PostgreSQL for production/staging and SQLite for local development)
var isProduction = builder.Environment.IsProduction() ||
                   builder.Configuration["ASPNETCORE_ENVIRONMENT"]?.Equals("Production", StringComparison.OrdinalIgnoreCase) == true;

var (databaseProvider, resolvedConnectionString) = ResolveDatabaseConfiguration(
    builder.Configuration,
    Environment.GetEnvironmentVariable("DATABASE_URL"),
    isProduction);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    if (databaseProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(resolvedConnectionString);
    }
    else
    {
        options.UseSqlite(resolvedConnectionString);
    }
});

// 2. ASP.NET Core Identity configuration
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 8;
    options.User.RequireUniqueEmail = true;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// 3. JWT Options, Production Security Guard & Authentication
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwtSection);
var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

// Production Security Guard: Prevent silent fallback to known development secrets
const string knownDevKey1 = "TrustVaultSuperSecretKeyForDevelopment2026!MustBeAtLeast32BytesLongForHmacSha256";
const string knownDevKey2 = "TrustVaultSuperSecretKeyForDevelopment2026!MustBeAtLeast32Bytes";

if (isProduction)
{
    if (string.IsNullOrWhiteSpace(jwtOptions.SecretKey) ||
        jwtOptions.SecretKey == knownDevKey1 ||
        jwtOptions.SecretKey == knownDevKey2 ||
        jwtOptions.SecretKey.Length < 32)
    {
        throw new InvalidOperationException(
            "CRITICAL SECURITY CONFIGURATION ERROR: Production deployment requires an explicit, secure JWT signing key. " +
            "Set the 'Jwt__SecretKey' environment variable to a cryptographically strong secret of at least 32 characters.");
    }

    var configuredAdminPassword = builder.Configuration["SeedData:AdminPassword"];
    if (configuredAdminPassword == "Admin#TrustVault2026!")
    {
        throw new InvalidOperationException(
            "CRITICAL SECURITY CONFIGURATION ERROR: The default development administrator password cannot be used in production. " +
            "Set the 'SeedData__AdminPassword' environment variable to a strong unique password, or remove SeedData:AdminPassword.");
    }
}

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = isProduction; // Enforce HTTPS metadata in production
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey)),
        ValidateIssuer = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtOptions.Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});

// 4. Authorization Policies and Resource-Based Authorization Handler
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAdminRole", policy => policy.RequireRole(Roles.Admin));
    options.AddPolicy("RequireOwnerRole", policy => policy.RequireRole(Roles.Owner, Roles.Admin));
    options.AddPolicy("RequireViewerRole", policy => policy.RequireRole(Roles.Viewer, Roles.Owner, Roles.Admin));
});

builder.Services.AddScoped<IAuthorizationHandler, DocumentAuthorizationHandler>();

// 5. Application Services & Document Storage Provider
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuditService, AuditService>();

ValidateStorageConfiguration(builder.Configuration, isProduction);

var storageProvider = builder.Configuration["Storage:Provider"]
    ?? Environment.GetEnvironmentVariable("STORAGE_PROVIDER")
    ?? "Local";

if (storageProvider.Equals("S3", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var serviceUrl = config["Storage:S3ServiceUrl"]
            ?? Environment.GetEnvironmentVariable("STORAGE_S3_SERVICE_URL");
        var region = config["Storage:S3Region"]
            ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
            ?? "us-east-1";
        var accessKey = config["Storage:S3AccessKey"]
            ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")
            ?? config["AWS_ACCESS_KEY_ID"];
        var secretKey = config["Storage:S3SecretKey"]
            ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")
            ?? config["AWS_SECRET_ACCESS_KEY"];

        var s3Config = new Amazon.S3.AmazonS3Config();
        if (!string.IsNullOrEmpty(serviceUrl))
        {
            s3Config.ServiceURL = serviceUrl;
            s3Config.ForcePathStyle = true; // Required for S3-compatible endpoints (Cloudflare R2, MinIO, Supabase)
            if (!string.IsNullOrWhiteSpace(region))
            {
                s3Config.AuthenticationRegion = region;
            }
        }
        else
        {
            s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
        {
            return new Amazon.S3.AmazonS3Client(accessKey, secretKey, s3Config);
        }

        return new Amazon.S3.AmazonS3Client(s3Config);
    });
    builder.Services.AddScoped<IDocumentStorageService, S3DocumentStorageService>();
}
else
{
    builder.Services.AddScoped<IDocumentStorageService, LocalSecureFileStorageService>();
}

builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

// 6. Controllers and Configurable Production CORS
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// Configurable Production CORS:
// Reads allowed origins from 'Cors:AllowedOrigins' or 'CORS_ALLOWED_ORIGINS' environment variable.
// Always keeps local development origins available for testing and pairing.
var defaultDevOrigins = new[]
{
    "http://localhost:5173",
    "http://localhost:3000",
    "http://localhost:4173",
    "https://localhost:5173",
    "https://localhost:3000"
};

var configuredOriginsStr = builder.Configuration["Cors:AllowedOrigins"] 
    ?? Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");

var allowedOrigins = new HashSet<string>(defaultDevOrigins, StringComparer.OrdinalIgnoreCase);

if (!string.IsNullOrWhiteSpace(configuredOriginsStr))
{
    var customOrigins = configuredOriginsStr.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var o in customOrigins)
    {
        if (Uri.TryCreate(o, UriKind.Absolute, out var uri))
        {
            allowedOrigins.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        else
        {
            allowedOrigins.Add(o);
        }
    }
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("TrustVaultCorsPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins.ToArray())
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// 7. Swagger / OpenAPI with JWT Authorization Support
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "TrustVault Secure Document API",
        Version = "v1",
        Description = "TrustVault Secure Document API - Zero-trust authentication, role-based governance, and resource-authorized document storage."
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Format: 'Bearer {token}'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// 8. Middleware Pipeline
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "TrustVault API v1");
        c.RoutePrefix = "swagger";
    });
}

// Correct CORS ordering: before Authentication & Authorization
app.UseCors("TrustVaultCorsPolicy");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// 9. Database Initialization and Seeding (non-blocking on connection failure)
try
{
    await DbInitializer.InitializeAsync(app.Services, app.Configuration, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Initial database migration/seeding encountered an issue. Backend will continue in degraded mode.");
}

app.Run();

// Required for WebApplicationFactory and external integration/unit tests
public partial class Program
{
    /// <summary>
    /// Resolves and normalizes the database provider and connection string from configuration sources
    /// (DATABASE_URL environment variable, builder.Configuration["DATABASE_URL"], ConnectionStrings:DefaultConnection, etc.).
    /// Guarantees that any PostgreSQL URI format (postgres:// or postgresql://) from ANY configuration source
    /// is converted into a valid Npgsql connection string, validated with NpgsqlConnectionStringBuilder,
    /// before EF Core receives it.
    /// </summary>
    public static (string Provider, string ConnectionString) ResolveDatabaseConfiguration(
        IConfiguration configuration,
        string? rawDatabaseUrl = null,
        bool isProduction = false)
    {
        // 1. Determine explicit or inferred database provider
        var configuredProvider = configuration["DatabaseProvider"];

        // 2. Discover potential PostgreSQL connection string candidates in priority order:
        //    Priority 1: rawDatabaseUrl parameter (e.g. Environment.GetEnvironmentVariable("DATABASE_URL"))
        //    Priority 2: configuration["DATABASE_URL"]
        //    Priority 3: configuration.GetConnectionString("DefaultConnection") (ConnectionStrings:DefaultConnection / ConnectionStrings__DefaultConnection)
        string? candidatePg = null;

        if (!string.IsNullOrWhiteSpace(rawDatabaseUrl))
        {
            candidatePg = rawDatabaseUrl.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(configuration["DATABASE_URL"]))
        {
            candidatePg = configuration["DATABASE_URL"]!.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
        {
            candidatePg = configuration.GetConnectionString("DefaultConnection")!.Trim();
        }

        // 3. Determine provider:
        // - If configuredProvider is explicitly given, honor it.
        // - Otherwise, if a PostgreSQL connection string candidate is present (and safe if in production), infer "PostgreSQL".
        // - Else default to "Sqlite".
        string provider;
        if (!string.IsNullOrWhiteSpace(configuredProvider))
        {
            provider = configuredProvider.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(candidatePg) && (!isProduction || !IsUnsafeDevelopmentConnectionString(candidatePg)))
        {
            provider = "PostgreSQL";
        }
        else
        {
            provider = "Sqlite";
        }

        if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(candidatePg))
            {
                if (isProduction)
                {
                    throw new InvalidOperationException(
                        "CRITICAL DATABASE CONFIGURATION ERROR: PostgreSQL was selected as the database provider in production, " +
                        "but neither DATABASE_URL nor ConnectionStrings__DefaultConnection was configured.");
                }

                // Development fallback
                candidatePg = "Host=localhost;Port=5432;Database=trustvault_db;Username=postgres;Password=postgres;";
            }

            // Normalize the connection string (handles postgres:// and postgresql:// URIs, strips quotes, configures SSL)
            string normalized = ParseDatabaseUrlToNpgsql(candidatePg, isProduction);

            // Validate that the normalized connection string conforms to Npgsql specification
            NpgsqlConnectionStringBuilder parsedBuilder;
            try
            {
                parsedBuilder = new NpgsqlConnectionStringBuilder(normalized);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "CRITICAL DATABASE CONFIGURATION ERROR: The configured PostgreSQL connection string does not conform to Npgsql specification. " +
                    "Ensure DATABASE_URL or ConnectionStrings__DefaultConnection is a valid URI or standard key-value connection string.", ex);
            }

            // In production, verify that the normalized connection string does not target localhost or use dev credentials
            if (isProduction && IsUnsafeDevelopmentConnectionString(normalized))
            {
                throw new InvalidOperationException(
                    "CRITICAL DATABASE CONFIGURATION ERROR: A development or localhost PostgreSQL connection string cannot be used in production. " +
                    "Ensure a valid production DATABASE_URL or ConnectionStrings__DefaultConnection is configured.");
            }

            return ("PostgreSQL", normalized);
        }
        else
        {
            if (isProduction)
            {
                throw new InvalidOperationException(
                    "CRITICAL DATABASE CONFIGURATION ERROR: SQLite is not permitted in production. " +
                    "PostgreSQL must be configured for production environments via DATABASE_URL or ConnectionStrings__DefaultConnection.");
            }

            var sqliteConn = configuration.GetConnectionString("SqliteConnection")
                ?? "Data Source=trustvault.db";
            return ("Sqlite", sqliteConn);
        }
    }

    /// <summary>
    /// Converts a DATABASE_URL (URI or standard connection string) into a secure Npgsql connection string.
    /// Uses NpgsqlConnectionStringBuilder to safely encode credentials, support colons/semicolons/hashes in passwords,
    /// and configure SSL settings for Supabase, Render, and cloud PostgreSQL providers.
    /// Supports both standard RFC 3986 URIs and database URIs containing unescaped special characters in credentials (e.g. '#', '@', '\', '?').
    /// </summary>
    public static string ParseDatabaseUrlToNpgsql(string databaseUrl, bool isProduction = false)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl)) return databaseUrl;

        var trimmed = databaseUrl.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
        }

        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // Validate port and percent-escapes in the URI before proceeding
        ValidatePortInUri(trimmed);
        ValidateDatabasePathInUri(trimmed);

        string? host = null;
        int port = 5432;
        string? database = null;
        string? username = null;
        string? password = null;
        string? queryString = null;

        // Attempt parsing via standard Uri.TryCreate first.
        // If the password contains unencoded '#', '?', or other special characters, Uri.TryCreate either fails,
        // misplaces credentials into Fragment, or corrupts query/userinfo.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            !uri.UserInfo.Contains('?') &&
            Uri.CheckHostName(uri.Host) != UriHostNameType.Unknown)
        {
            host = uri.Host;
            if (uri.Port > 0) port = uri.Port;
            var db = uri.AbsolutePath.TrimStart('/');
            if (!string.IsNullOrWhiteSpace(db)) database = SafeUnescapeDatabase(db);

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var colonIndex = uri.UserInfo.IndexOf(':');
                if (colonIndex >= 0)
                {
                    username = SafeUnescape(uri.UserInfo.Substring(0, colonIndex));
                    password = SafeUnescape(uri.UserInfo.Substring(colonIndex + 1));
                }
                else
                {
                    username = SafeUnescape(uri.UserInfo);
                }
            }

            if (!string.IsNullOrEmpty(uri.Query))
            {
                queryString = uri.Query.TrimStart('?');
            }
        }
        else
        {
            // Robust direct extraction for database URIs with unencoded special characters in credentials
            ParsePostgresUriDirect(trimmed, out host, out port, out database, out username, out password, out queryString);
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("The provided DATABASE_URL URI must specify a valid host.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.Database = database;
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            builder.Username = username;
        }

        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        // Default SSL configuration: Require encrypted connections to cloud PostgreSQL providers.
        // Supabase, Render, Neon, and Railway all require SSL for external connections.
        builder.SslMode = SslMode.Require;

        // Parse supported query parameters if present (e.g. ?sslmode=require&pooling=true).
        // Only an explicit allowlist of safe parameters is accepted; unknown parameters are
        // silently ignored to prevent URI query strings from changing security behavior.
        if (!string.IsNullOrWhiteSpace(queryString))
        {
            ApplyQueryParameters(builder, queryString, isProduction);
        }

        return builder.ConnectionString;
    }

    private static void ParsePostgresUriDirect(
        string uriString,
        out string? host,
        out int port,
        out string? database,
        out string? username,
        out string? password,
        out string? queryString)
    {
        host = null;
        port = 5432;
        database = null;
        username = null;
        password = null;
        queryString = null;

        int schemeEnd = uriString.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            throw new ArgumentException("The provided DATABASE_URL is not a valid absolute URI.");
        }

        string remainder = uriString.Substring(schemeEnd + 3);
        if (string.IsNullOrWhiteSpace(remainder))
        {
            throw new ArgumentException("The provided DATABASE_URL is not a valid absolute URI.");
        }

        // 1. Separate userinfo from the remainder of the URI.
        // In a database URI, authority credentials precede the LAST '@' before the host.
        // This ensures unencoded '?', '#', ':', ';', or backslashes in passwords are not confused with query or path.
        int atIndex = remainder.LastIndexOf('@');
        string afterAuthority;
        if (atIndex >= 0)
        {
            string userInfo = remainder.Substring(0, atIndex);
            afterAuthority = remainder.Substring(atIndex + 1);

            int colonIndex = userInfo.IndexOf(':');
            if (colonIndex >= 0)
            {
                var u = SafeUnescape(userInfo.Substring(0, colonIndex));
                var p = SafeUnescape(userInfo.Substring(colonIndex + 1));
                if (!string.IsNullOrEmpty(u)) username = u;
                if (!string.IsNullOrEmpty(p)) password = p;
            }
            else
            {
                var u = SafeUnescape(userInfo);
                if (!string.IsNullOrEmpty(u)) username = u;
            }
        }
        else
        {
            afterAuthority = remainder;
        }

        // 2. Separate query string (?...) from afterAuthority
        int queryIndex = afterAuthority.IndexOf('?');
        if (queryIndex >= 0)
        {
            queryString = afterAuthority.Substring(queryIndex + 1);
            afterAuthority = afterAuthority.Substring(0, queryIndex);
        }

        // 3. Separate database path (/...) from afterAuthority
        int pathIndex = afterAuthority.IndexOf('/');
        if (pathIndex >= 0)
        {
            var dbPart = afterAuthority.Substring(pathIndex + 1).Trim();
            if (!string.IsNullOrEmpty(dbPart))
            {
                database = SafeUnescapeDatabase(dbPart);
            }
            afterAuthority = afterAuthority.Substring(0, pathIndex);
        }

        // 4. Parse host and port from the remaining authority component
        string hostPortPart = afterAuthority;
        if (!string.IsNullOrWhiteSpace(hostPortPart))
        {
            if (hostPortPart.StartsWith("["))
            {
                int closeBracket = hostPortPart.IndexOf(']');
                if (closeBracket > 0)
                {
                    host = hostPortPart.Substring(0, closeBracket + 1);
                    int portColon = hostPortPart.IndexOf(':', closeBracket);
                    if (portColon > 0)
                    {
                        var portStr = hostPortPart.Substring(portColon + 1);
                        if (int.TryParse(portStr, out int p) && p > 0 && p <= 65535)
                        {
                            port = p;
                        }
                        else
                        {
                            throw new ArgumentException("The provided DATABASE_URL contains an invalid port.");
                        }
                    }
                }
                else
                {
                    throw new ArgumentException("The provided DATABASE_URL contains an invalid IPv6 host format.");
                }
            }
            else
            {
                int colonIndex = hostPortPart.LastIndexOf(':');
                if (colonIndex >= 0)
                {
                    host = hostPortPart.Substring(0, colonIndex);
                    var portStr = hostPortPart.Substring(colonIndex + 1);
                    if (int.TryParse(portStr, out int p) && p > 0 && p <= 65535)
                    {
                        port = p;
                    }
                    else
                    {
                        throw new ArgumentException("The provided DATABASE_URL contains an invalid port.");
                    }
                }
                else
                {
                    host = hostPortPart;
                }
            }
        }

        // Validate host presence and format
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("The provided DATABASE_URL URI must specify a valid host.");
        }

        if (host.Contains(':') && !(host.StartsWith('[') && host.EndsWith(']')))
        {
            throw new ArgumentException("The provided DATABASE_URL is not a valid absolute URI.");
        }

        if (host.Contains(' ') || host.Contains('\t') || host.Contains('\r') || host.Contains('\n'))
        {
            throw new ArgumentException("The provided DATABASE_URL contains an invalid host.");
        }
    }

    private static string SafeUnescape(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            // For passwords containing unencoded '%', preserve the raw character sequence
            return value;
        }
    }

    private static string SafeUnescapeDatabase(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            throw new ArgumentException("The provided DATABASE_URL contains an invalid database name with malformed percent escapes.");
        }
    }

    private static void ApplyQueryParameters(NpgsqlConnectionStringBuilder builder, string queryString, bool isProduction = false)
    {
        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var eqIndex = pair.IndexOf('=');
            var key = eqIndex > 0 ? SafeUnescape(pair.Substring(0, eqIndex)).Trim() : SafeUnescape(pair).Trim();
            var val = eqIndex > 0 ? SafeUnescape(pair.Substring(eqIndex + 1)).Trim() : string.Empty;

            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<SslMode>(val, true, out var parsedMode))
                {
                    throw new ArgumentException($"The provided DATABASE_URL contains an invalid or unrecognized sslmode: '{val}'.");
                }

                if (isProduction && (parsedMode == SslMode.Disable || parsedMode == SslMode.Allow || parsedMode == SslMode.Prefer))
                {
                    throw new InvalidOperationException(
                        $"CRITICAL DATABASE CONFIGURATION ERROR: Insecure SSL mode '{parsedMode}' is not permitted in production. " +
                        "PostgreSQL connections in production must enforce TLS (Require, VerifyCA, or VerifyFull).");
                }

                builder.SslMode = parsedMode;
            }
            else if (key.Equals("channel_binding", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("channelbinding", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<ChannelBinding>(val, true, out var channelBinding))
                {
                    builder.ChannelBinding = channelBinding;
                }
            }
            else if (key.Equals("pooling", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(val, out var pooling))
                {
                    builder.Pooling = pooling;
                }
            }
        }
    }

    private static bool IsUnsafeDevelopmentConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return true;

        try
        {
            var normalized = ParseDatabaseUrlToNpgsql(connectionString, isProduction: false);
            var builder = new NpgsqlConnectionStringBuilder(normalized);

            if (IsLocalhostOrLoopback(builder.Host))
            {
                return true;
            }

            if (string.Equals(builder.Username, "postgres", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(builder.Password, "postgres", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLocalhostOrLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var trimmed = host.Trim().Trim('[', ']');
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed == "127.0.0.1" || trimmed == "::1" || trimmed == "0.0.0.0") return true;
        if (System.Net.IPAddress.TryParse(trimmed, out var ip) && System.Net.IPAddress.IsLoopback(ip)) return true;
        return false;
    }

    private static void ValidatePortInUri(string uriString)
    {
        int schemeEnd = uriString.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return;

        string rest = uriString.Substring(schemeEnd + 3);
        int atIndex = rest.LastIndexOf('@');
        string afterAuth = atIndex >= 0 ? rest.Substring(atIndex + 1) : rest;

        int endHostPort = afterAuth.IndexOfAny(new[] { '/', '?' });
        string hostPort = endHostPort >= 0 ? afterAuth.Substring(0, endHostPort) : afterAuth;

        if (hostPort.StartsWith("["))
        {
            int closeBracket = hostPort.IndexOf(']');
            if (closeBracket > 0)
            {
                int colonIndex = hostPort.IndexOf(':', closeBracket);
                if (colonIndex > 0)
                {
                    string portStr = hostPort.Substring(colonIndex + 1);
                    if (!int.TryParse(portStr, out int p) || p <= 0 || p > 65535)
                    {
                        throw new ArgumentException("The provided DATABASE_URL contains an invalid port.");
                    }
                }
            }
            else
            {
                throw new ArgumentException("The provided DATABASE_URL contains an invalid IPv6 host format.");
            }
        }
        else
        {
            int colonIndex = hostPort.LastIndexOf(':');
            if (colonIndex >= 0)
            {
                string portStr = hostPort.Substring(colonIndex + 1);
                if (!int.TryParse(portStr, out int p) || p <= 0 || p > 65535)
                {
                    throw new ArgumentException("The provided DATABASE_URL contains an invalid port.");
                }
            }
        }
    }

    private static void ValidateDatabasePathInUri(string uriString)
    {
        int schemeEnd = uriString.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return;

        string rest = uriString.Substring(schemeEnd + 3);
        int atIndex = rest.LastIndexOf('@');
        string afterAuth = atIndex >= 0 ? rest.Substring(atIndex + 1) : rest;

        int slashIndex = afterAuth.IndexOf('/');
        if (slashIndex >= 0)
        {
            string pathAndQuery = afterAuth.Substring(slashIndex + 1);
            int qIndex = pathAndQuery.IndexOf('?');
            string dbName = qIndex >= 0 ? pathAndQuery.Substring(0, qIndex) : pathAndQuery;
            dbName = dbName.Trim();
            if (!string.IsNullOrEmpty(dbName) && HasInvalidPercentEscape(dbName))
            {
                throw new ArgumentException("The provided DATABASE_URL contains an invalid database name with malformed percent escapes.");
            }
        }
    }

    private static bool HasInvalidPercentEscape(string str)
    {
        for (int i = 0; i < str.Length; i++)
        {
            if (str[i] == '%')
            {
                if (i + 2 >= str.Length || !IsHex(str[i + 1]) || !IsHex(str[i + 2]))
                {
                    return true;
                }
                i += 2;
            }
        }
        return false;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// Validates storage provider configuration during startup.
    /// Ensures that when remote S3/Supabase storage is configured, all required settings (bucket name, credentials, service URL)
    /// are properly specified and valid. Never logs or leaks credentials.
    /// </summary>
    public static void ValidateStorageConfiguration(IConfiguration configuration, bool isProduction = false)
    {
        var provider = configuration["Storage:Provider"]
            ?? Environment.GetEnvironmentVariable("STORAGE_PROVIDER")
            ?? "Local";

        if (provider.Equals("S3", StringComparison.OrdinalIgnoreCase))
        {
            var bucketName = configuration["Storage:S3BucketName"]
                ?? Environment.GetEnvironmentVariable("STORAGE_S3_BUCKET_NAME");
            if (string.IsNullOrWhiteSpace(bucketName))
            {
                throw new InvalidOperationException(
                    "CRITICAL STORAGE CONFIGURATION ERROR: When Storage:Provider is set to 'S3', " +
                    "'Storage:S3BucketName' (or 'Storage__S3BucketName') must be configured.");
            }

            var accessKey = configuration["Storage:S3AccessKey"]
                ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")
                ?? configuration["AWS_ACCESS_KEY_ID"];
            var secretKey = configuration["Storage:S3SecretKey"]
                ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")
                ?? configuration["AWS_SECRET_ACCESS_KEY"];

            if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            {
                throw new InvalidOperationException(
                    "CRITICAL STORAGE CONFIGURATION ERROR: S3 storage provider is configured, but access credentials are missing. " +
                    "Set 'AWS_ACCESS_KEY_ID' (or 'Storage:S3AccessKey') and 'AWS_SECRET_ACCESS_KEY' (or 'Storage:S3SecretKey').");
            }

            var serviceUrl = configuration["Storage:S3ServiceUrl"]
                ?? Environment.GetEnvironmentVariable("STORAGE_S3_SERVICE_URL");
            if (string.IsNullOrWhiteSpace(serviceUrl))
            {
                throw new InvalidOperationException(
                    "CRITICAL STORAGE CONFIGURATION ERROR: When Storage:Provider is set to 'S3', " +
                    "'Storage:S3ServiceUrl' (or 'Storage__S3ServiceUrl') must be configured with a valid S3 endpoint URL.");
            }

            if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "CRITICAL STORAGE CONFIGURATION ERROR: 'Storage:S3ServiceUrl' must be a valid absolute HTTP or HTTPS URL.");
            }

            if (isProduction && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "CRITICAL STORAGE CONFIGURATION ERROR: S3 service endpoint cannot target localhost or loopback in production.");
            }

            var region = configuration["Storage:S3Region"]
                ?? Environment.GetEnvironmentVariable("AWS_REGION")
                ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
            if (string.IsNullOrWhiteSpace(region))
            {
                throw new InvalidOperationException(
                    "CRITICAL STORAGE CONFIGURATION ERROR: When Storage:Provider is set to 'S3', " +
                    "an explicit S3 signing region ('Storage:S3Region', 'Storage__S3Region', or 'AWS_REGION') must be configured to match your Supabase project region.");
            }
        }
        else if (!provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"CRITICAL STORAGE CONFIGURATION ERROR: Unsupported storage provider '{provider}'. Supported providers are 'Local' and 'S3'.");
        }
    }
}
