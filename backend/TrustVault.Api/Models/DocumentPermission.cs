namespace TrustVault.Api.Models;

public class DocumentPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentId { get; set; }
    public virtual Document? Document { get; set; }

    public Guid UserId { get; set; }
    public virtual ApplicationUser? User { get; set; }

    public PermissionLevel PermissionLevel { get; set; } = PermissionLevel.Read;

    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid GrantedById { get; set; }
}
