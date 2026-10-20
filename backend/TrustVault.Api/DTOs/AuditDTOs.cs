namespace TrustVault.Api.DTOs;

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

public record AuditVerificationResultDto(
    bool IsChainValid,
    int VerifiedRecordsCount,
    string? ViolationDetails,
    DateTime CheckedAtUtc
);
