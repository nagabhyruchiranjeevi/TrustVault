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
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var databaseProvider = builder.Configuration["DatabaseProvider"] 
    ?? (!string.IsNullOrEmpty(databaseUrl) ? "PostgreSQL" : "Sqlite");

string pgConnectionString;
if (!string.IsNullOrEmpty(databaseUrl))
{
    pgConnectionString = ParseDatabaseUrlToNpgsql(databaseUrl);
}
else
{
    pgConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
        ?? "Host=localhost;Port=5432;Database=trustvault_db;Username=postgres;Password=postgres;";
}

var sqliteConnectionString = builder.Configuration.GetConnectionString("SqliteConnection") 
    ?? "Data Source=trustvault.db";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    if (databaseProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(pgConnectionString);
    }
    else
    {
        options.UseSqlite(sqliteConnectionString);
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
var isProduction = builder.Environment.IsProduction() ||
                   builder.Configuration["ASPNETCORE_ENVIRONMENT"]?.Equals("Production", StringComparison.OrdinalIgnoreCase) == true;

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

var storageProvider = builder.Configuration["Storage:Provider"] ?? "Local";
if (storageProvider.Equals("S3", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var serviceUrl = config["Storage:S3ServiceUrl"] ?? Environment.GetEnvironmentVariable("STORAGE_S3_SERVICE_URL");
        var region = config["Storage:S3Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1";
        var accessKey = config["Storage:S3AccessKey"] ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretKey = config["Storage:S3SecretKey"] ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

        var s3Config = new Amazon.S3.AmazonS3Config();
        if (!string.IsNullOrEmpty(serviceUrl))
        {
            s3Config.ServiceURL = serviceUrl;
            s3Config.ForcePathStyle = true; // Required for S3-compatible endpoints (Cloudflare R2, MinIO, Supabase)
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
    /// Converts a DATABASE_URL (URI or standard connection string) into a secure Npgsql connection string.
    /// Uses NpgsqlConnectionStringBuilder to safely encode credentials, support colons/semicolons in passwords,
    /// and configure SSL settings for Supabase, Render, and cloud PostgreSQL providers.
    /// </summary>
    public static string ParseDatabaseUrlToNpgsql(string databaseUrl)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl)) return databaseUrl;

        var trimmed = databaseUrl.Trim();
        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return databaseUrl;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The provided DATABASE_URL is not a valid absolute URI.");
        }

        var builder = new NpgsqlConnectionStringBuilder();

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("The provided DATABASE_URL URI must specify a valid host.");
        }

        builder.Host = uri.Host;

        if (uri.Port > 0)
        {
            builder.Port = uri.Port;
        }
        else
        {
            builder.Port = 5432;
        }

        var database = uri.AbsolutePath.TrimStart('/');
        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.Database = Uri.UnescapeDataString(database);
        }

        var userInfo = uri.UserInfo;
        if (!string.IsNullOrEmpty(userInfo))
        {
            var colonIndex = userInfo.IndexOf(':');
            if (colonIndex >= 0)
            {
                builder.Username = Uri.UnescapeDataString(userInfo.Substring(0, colonIndex));
                builder.Password = Uri.UnescapeDataString(userInfo.Substring(colonIndex + 1));
            }
            else
            {
                builder.Username = Uri.UnescapeDataString(userInfo);
            }
        }

        // Default SSL configuration: Require encrypted connections to cloud PostgreSQL providers.
        // Supabase, Render, Neon, and Railway all require SSL for external connections.
        builder.SslMode = SslMode.Require;

        // Parse supported query parameters if present (e.g. ?sslmode=require&pooling=true).
        // Only an explicit allowlist of safe parameters is accepted; unknown parameters are
        // silently ignored to prevent URI query strings from changing security behavior.
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var query = uri.Query.TrimStart('?');
            var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var pair in pairs)
            {
                var eqIndex = pair.IndexOf('=');
                if (eqIndex > 0)
                {
                    var key = Uri.UnescapeDataString(pair.Substring(0, eqIndex)).Trim();
                    var val = Uri.UnescapeDataString(pair.Substring(eqIndex + 1)).Trim();

                    if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<SslMode>(val, true, out var parsedMode))
                        {
                            builder.SslMode = parsedMode;
                        }
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
                    // All other query parameters are silently ignored to prevent
                    // unrecognized keys from altering connection security settings.
                }
            }
        }

        return builder.ConnectionString;
    }
}
