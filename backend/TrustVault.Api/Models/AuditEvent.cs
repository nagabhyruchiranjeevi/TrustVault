namespace TrustVault.Api.Models;

public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? UserId { get; set; }
    public virtual ApplicationUser? User { get; set; }

    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string? EntityId { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public string? IpAddress { get; set; }

    public string? Details { get; set; }

    public string Status { get; set; } = "Success"; // Success, Failure, Forbidden
}
