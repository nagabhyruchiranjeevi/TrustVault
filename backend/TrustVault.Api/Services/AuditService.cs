using TrustVault.Api.Data;
using TrustVault.Api.Models;

namespace TrustVault.Api.Services;

public interface IAuditService
{
    Task LogEventAsync(Guid? userId, string action, string entityType, string? entityId, string? details, string status, string? ipAddress = null);
}

public class AuditService : IAuditService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<AuditService> _logger;

    public AuditService(ApplicationDbContext dbContext, ILogger<AuditService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task LogEventAsync(Guid? userId, string action, string entityType, string? entityId, string? details, string status, string? ipAddress = null)
    {
        try
        {
            var auditEvent = new AuditEvent
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                Status = status,
                IpAddress = ipAddress,
                TimestampUtc = DateTime.UtcNow
            };

            _dbContext.AuditEvents.Add(auditEvent);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist audit event for Action: {Action}, Entity: {EntityType}", action, entityType);
        }
    }
}
