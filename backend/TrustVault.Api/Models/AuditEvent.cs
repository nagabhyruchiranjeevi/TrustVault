using System.Security.Cryptography;
using System.Text;

namespace TrustVault.Api.Models;

public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public long SequenceNumber { get; set; }

    public Guid? UserId { get; set; }
    public virtual ApplicationUser? User { get; set; }

    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string? EntityId { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public string? IpAddress { get; set; }

    public string? CorrelationId { get; set; }

    public string? Details { get; set; }

    public string Status { get; set; } = "Success"; // Success, Failure, Forbidden

    // Cryptographic Hash Chain properties for tamper evidence
    public string PreviousHash { get; set; } = string.Empty;
    public string CurrentHash { get; set; } = string.Empty;

    public static string ComputeDeterministicHash(
        string previousHash,
        long sequenceNumber,
        Guid id,
        DateTime timestampUtc,
        Guid? userId,
        string action,
        string entityType,
        string? entityId,
        string status,
        string? details,
        string? correlationId)
    {
        // Canonical deterministic string representation with fixed millisecond ISO-8601 formatting.
        // Guarantee UTC interpretation without timezone shifts when EF Core loads Unspecified kinds from SQLite.
        var utc = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);
        var canonicalTimestamp = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var rawPayload = string.Join("|",
            previousHash ?? string.Empty,
            sequenceNumber.ToString(),
            id.ToString(),
            canonicalTimestamp,
            userId?.ToString() ?? "ANONYMOUS",
            action ?? string.Empty,
            entityType ?? string.Empty,
            entityId ?? string.Empty,
            status ?? string.Empty,
            details ?? string.Empty,
            correlationId ?? string.Empty
        );

        var bytes = Encoding.UTF8.GetBytes(rawPayload);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
