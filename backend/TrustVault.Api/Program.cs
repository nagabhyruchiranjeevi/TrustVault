using System.Text;
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

// 1. Database & EF Core setup (Supports PostgreSQL for production/staging and SQLite for local development)
var databaseProvider = builder.Configuration["DatabaseProvider"] ?? "Sqlite";
var pgConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=localhost;Port=5432;Database=trustvault_db;Username=postgres;Password=postgres;";
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

// 3. JWT Options & Authentication
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwtSection);
var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false; // Enabled in dev, configurable for prod
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

// 5. Application Services & Validators
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IDocumentStorageService, LocalSecureFileStorageService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

// 6. Controllers and CORS
builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddPolicy("TrustVaultCorsPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
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
        Description = "TrustVault Day 1 Core API - Secure authentication, role-based governance, and resource-authorized document storage."
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

// Required for WebApplicationFactory in integration tests
public partial class Program { }
