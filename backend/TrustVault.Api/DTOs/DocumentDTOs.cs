using TrustVault.Api.Models;

namespace TrustVault.Api.DTOs;

public record CreateDocumentDto(
    string Title,
    string FileName,
    string ContentType,
    string Base64Content
);

public record UpdateDocumentDto(
    string Title
);

public record DocumentDto(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    Guid OwnerId,
    string? OwnerEmail,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    bool IsOwner,
    string? EffectivePermission
);

public record GrantPermissionDto(
    string UserEmail,
    PermissionLevel PermissionLevel
);

public record DocumentPermissionDto(
    Guid Id,
    Guid DocumentId,
    Guid UserId,
    string UserEmail,
    string PermissionLevel,
    DateTime GrantedAtUtc
);
