using Microsoft.AspNetCore.Identity;
using TrustVault.Api.Auth;
using TrustVault.Api.DTOs;
using TrustVault.Api.Models;

namespace TrustVault.Api.Services;

public interface IAuthService
{
    Task<(bool Succeeded, string? Error, AuthResponse? Response)> RegisterAsync(RegisterRequest request, string? ipAddress = null);
    Task<(bool Succeeded, string? Error, AuthResponse? Response)> LoginAsync(LoginRequest request, string? ipAddress = null);
    Task<UserDto?> GetUserByIdAsync(Guid userId);
}

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IAuditService _auditService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        IAuditService auditService,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<(bool Succeeded, string? Error, AuthResponse? Response)> RegisterAsync(RegisterRequest request, string? ipAddress = null)
    {
        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser != null)
        {
            await _auditService.LogEventAsync(null, "Register", "User", null, $"Email already registered: {request.Email}", "Failure", ipAddress);
            return (false, "An account with this email address already exists.", null);
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            await _auditService.LogEventAsync(null, "Register", "User", null, $"Creation failed: {errors}", "Failure", ipAddress);
            return (false, errors, null);
        }

        // Assign initial role (default to Owner or requested Viewer/Owner)
        var roleToAssign = string.Equals(request.Role, Roles.Viewer, StringComparison.OrdinalIgnoreCase)
            ? Roles.Viewer
            : Roles.Owner;

        await _userManager.AddToRoleAsync(user, roleToAssign);
        var roles = await _userManager.GetRolesAsync(user);

        var (token, expiresAt) = _tokenService.GenerateToken(user, roles);
        var userDto = new UserDto(user.Id, user.Email!, user.FullName, roles, user.CreatedAtUtc);

        await _auditService.LogEventAsync(user.Id, "Register", "User", user.Id.ToString(), $"Registered user with role {roleToAssign}", "Success", ipAddress);

        return (true, null, new AuthResponse(token, expiresAt, userDto));
    }

    public async Task<(bool Succeeded, string? Error, AuthResponse? Response)> LoginAsync(LoginRequest request, string? ipAddress = null)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null || !user.IsActive)
        {
            await _auditService.LogEventAsync(null, "Login", "User", null, $"Invalid login attempt for {request.Email}", "Failure", ipAddress);
            return (false, "Invalid email or password.", null);
        }

        var isPasswordValid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!isPasswordValid)
        {
            await _auditService.LogEventAsync(user.Id, "Login", "User", user.Id.ToString(), "Invalid password provided", "Failure", ipAddress);
            return (false, "Invalid email or password.", null);
        }

        var roles = await _userManager.GetRolesAsync(user);
        var (token, expiresAt) = _tokenService.GenerateToken(user, roles);
        var userDto = new UserDto(user.Id, user.Email!, user.FullName, roles, user.CreatedAtUtc);

        await _auditService.LogEventAsync(user.Id, "Login", "User", user.Id.ToString(), "Successful authentication", "Success", ipAddress);

        return (true, null, new AuthResponse(token, expiresAt, userDto));
    }

    public async Task<UserDto?> GetUserByIdAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null) return null;

        var roles = await _userManager.GetRolesAsync(user);
        return new UserDto(user.Id, user.Email!, user.FullName, roles, user.CreatedAtUtc);
    }
}
