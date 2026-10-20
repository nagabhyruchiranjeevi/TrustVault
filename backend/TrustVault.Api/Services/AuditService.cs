using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Data;
using TrustVault.Api.Models;

namespace TrustVault.Api.Services;

public interface IAuditService
{
    Task<AuditEvent> LogEventAsync(
        Guid? userId,
        string action,
        string entityType,
        string? entityId,
        string? details,
        string status,
        string? ipAddress = null,
        string? correlationId = null);

    Task<(bool IsValid, string? ViolationDetails, int VerifiedCount)> VerifyChainIntegrityAsync();
}

public class AuditService : IAuditService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<AuditService> _logger;
    private static readonly SemaphoreSlim _chainLock = new(1, 1);

    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public AuditService(ApplicationDbContext dbContext, ILogger<AuditService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<AuditEvent> LogEventAsync(
        Guid? userId,
        string action,
        string entityType,
        string? entityId,
        string? details,
        string status,
        string? ipAddress = null,
        string? correlationId = null)
    {
        await _chainLock.WaitAsync();
        try
        {
            var latest = await _dbContext.AuditEvents
                .OrderByDescending(a => a.SequenceNumber)
                .FirstOrDefaultAsync();

            var sequenceNumber = latest == null ? 1 : latest.SequenceNumber + 1;
            var previousHash = latest == null ? GenesisHash : latest.CurrentHash;

            var id = Guid.NewGuid();
            var timestamp = DateTime.UtcNow;

            var currentHash = AuditEvent.ComputeDeterministicHash(
                previousHash,
                sequenceNumber,
                id,
                timestamp,
                userId,
                action,
                entityType,
                entityId,
                status,
                details,
                correlationId
            );

            var auditEvent = new AuditEvent
            {
                Id = id,
                SequenceNumber = sequenceNumber,
                UserId = userId,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                Status = status,
                IpAddress = ipAddress,
                CorrelationId = correlationId,
                TimestampUtc = timestamp,
                PreviousHash = previousHash,
                CurrentHash = currentHash
            };

            _dbContext.AuditEvents.Add(auditEvent);
            await _dbContext.SaveChangesAsync();

            return auditEvent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist audit event for Action: {Action}, Entity: {EntityType}", action, entityType);
            throw;
        }
        finally
        {
            _chainLock.Release();
        }
    }

    public async Task<(bool IsValid, string? ViolationDetails, int VerifiedCount)> VerifyChainIntegrityAsync()
    {
        var events = await _dbContext.AuditEvents
            .AsNoTracking()
            .OrderBy(a => a.SequenceNumber)
            .ToListAsync();

        if (events.Count == 0)
        {
            return (true, null, 0);
        }

        var expectedPreviousHash = GenesisHash;

        for (int i = 0; i < events.Count; i++)
        {
            var evt = events[i];

            // 1. Check PreviousHash link
            if (evt.PreviousHash != expectedPreviousHash)
            {
                var violation = $"Hash chain broken at Sequence #{evt.SequenceNumber} (Event Id: {evt.Id}). Expected PreviousHash: {expectedPreviousHash}, Found: {evt.PreviousHash}";
                _logger.LogCritical("AUDIT INTEGRITY VIOLATION: {Violation}", violation);
                return (false, violation, i);
            }

            // 2. Recompute deterministic hash
            var computedHash = AuditEvent.ComputeDeterministicHash(
                evt.PreviousHash,
                evt.SequenceNumber,
                evt.Id,
                evt.TimestampUtc,
                evt.UserId,
                evt.Action,
                evt.EntityType,
                evt.EntityId,
                evt.Status,
                evt.Details,
                evt.CorrelationId
            );

            if (computedHash != evt.CurrentHash)
            {
                var violation = $"Data tampering detected at Sequence #{evt.SequenceNumber} (Event Id: {evt.Id}). Recomputed: {computedHash}, Stored: {evt.CurrentHash}";
                _logger.LogCritical("AUDIT INTEGRITY VIOLATION: {Violation}", violation);
                return (false, violation, i);
            }

            expectedPreviousHash = evt.CurrentHash;
        }

        return (true, null, events.Count);
    }
}
