namespace TrustVault.Api.DTOs;

public record AuditEventDto(
    Guid Id,
    Guid? UserId,
    string? UserEmail,
    string Action,
    string EntityType,
    string? EntityId,
    DateTime TimestampUtc,
    string? IpAddress,
    string? Details,
    string Status
);
