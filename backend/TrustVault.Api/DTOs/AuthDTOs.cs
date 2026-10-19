namespace TrustVault.Api.DTOs;

public record RegisterRequest(
    string Email,
    string Password,
    string FullName,
    string? Role = null
);

public record LoginRequest(
    string Email,
    string Password
);

public record UserDto(
    Guid Id,
    string Email,
    string FullName,
    IList<string> Roles,
    DateTime CreatedAtUtc
);

public record AuthResponse(
    string Token,
    DateTime ExpiresAtUtc,
    UserDto User
);
