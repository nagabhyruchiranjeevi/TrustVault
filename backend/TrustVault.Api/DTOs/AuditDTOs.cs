namespace TrustVault.Api.DTOs;

/// <summary>
/// Full audit event DTO including cryptographic hash chain fields.
/// Returned only to Admin users - never exposed to regular users.
/// </summary>
public record AuditEventDto(
    Guid Id,
    long SequenceNumber,
    Guid? UserId,
    string? UserEmail,
    string Action,
    string EntityType,
    string? EntityId,
    DateTime TimestampUtc,
    string? IpAddress,
    string? CorrelationId,
    string? Details,
    string Status,
    string PreviousHash,
    string CurrentHash
);

/// <summary>
/// Redacted audit event DTO for the authenticated user's own self-audit view.
/// Cryptographic hashes are intentionally excluded to prevent hash reconstruction attacks.
/// IP address is also excluded to avoid leaking network metadata back to the user.
/// </summary>
public record AuditEventSummaryDto(
    Guid Id,
    long SequenceNumber,
    string Action,
    string EntityType,
    string? EntityId,
    DateTime TimestampUtc,
    string? Details,
    string Status
);

public record AuditVerificationResultDto(
    bool IsChainValid,
    int VerifiedRecordsCount,
    string? ViolationDetails,
    DateTime CheckedAtUtc
);
