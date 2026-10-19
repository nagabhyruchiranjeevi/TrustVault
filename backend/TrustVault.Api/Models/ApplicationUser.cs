using Microsoft.AspNetCore.Identity;

namespace TrustVault.Api.Models;

public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    // Navigation properties
    public virtual ICollection<Document> OwnedDocuments { get; set; } = new List<Document>();
    public virtual ICollection<DocumentPermission> Permissions { get; set; } = new List<DocumentPermission>();
}
