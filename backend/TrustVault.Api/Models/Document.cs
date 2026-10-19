namespace TrustVault.Api.Models;

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    // Internal secure storage identifier or non-public path (never exposed raw to client)
    public string StorageKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long FileSizeBytes { get; set; }

    public Guid OwnerId { get; set; }
    public virtual ApplicationUser? Owner { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    public bool IsDeleted { get; set; } = false;

    public virtual ICollection<DocumentPermission> Permissions { get; set; } = new List<DocumentPermission>();
}
