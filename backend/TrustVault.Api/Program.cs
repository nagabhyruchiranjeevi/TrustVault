using System.Text;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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

// Helper method to parse DATABASE_URL (in URI format) to Npgsql connection string format
static string ParseDatabaseUrlToNpgsql(string databaseUrl)
{
    if (string.IsNullOrWhiteSpace(databaseUrl)) return databaseUrl;
    if (!databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return databaseUrl;
    }

    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':');
    var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "";
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
    var port = uri.Port > 0 ? uri.Port : 5432;
    var database = uri.AbsolutePath.TrimStart('/');

    return $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true;";
}

// Required for WebApplicationFactory in integration tests
public partial class Program { }
