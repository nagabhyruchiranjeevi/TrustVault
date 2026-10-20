using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Data;
using TrustVault.Api.DTOs;
using TrustVault.Api.Models;
using TrustVault.Api.Services;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public AuditController(ApplicationDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    // -------------------------------------------------------------------------
    // ADMIN ONLY: Full audit log with cryptographic hashes (Admin role required)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns recent audit events INCLUDING hash chain data. Admin access only.
    /// Users cannot retrieve events belonging to other users through this endpoint.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> GetAuditLogs([FromQuery] int limit = 50)
    {
        var cappedLimit = Math.Clamp(limit, 1, 200);

        var logs = await _dbContext.AuditEvents
            .AsNoTracking()
            .Include(a => a.User)
            .OrderByDescending(a => a.SequenceNumber)
            .Take(cappedLimit)
            .Select(a => new AuditEventDto(
                a.Id,
                a.SequenceNumber,
                a.UserId,
                a.User != null ? a.User.Email : null,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.TimestampUtc,
                a.IpAddress,
                a.CorrelationId,
                a.Details,
                a.Status,
                a.PreviousHash,
                a.CurrentHash
            ))
            .ToListAsync();

        return Ok(logs);
    }

    /// <summary>
    /// Verifies the cryptographic hash-chain integrity of the entire audit log.
    /// Admin access only.
    /// </summary>
    [HttpGet("verify")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> VerifyIntegrity()
    {
        var (isValid, violation, count) = await _auditService.VerifyChainIntegrityAsync();

        var result = new AuditVerificationResultDto(
            isValid,
            count,
            violation,
            DateTime.UtcNow
        );

        if (!isValid)
        {
            return StatusCode(StatusCodes.Status409Conflict, result);
        }

        return Ok(result);
    }

    // -------------------------------------------------------------------------
    // USER SELF-AUDIT: Authenticated user can view their OWN events only.
    // Hash values are NOT exposed to prevent chain reconstruction attacks.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns audit events for the currently authenticated user only.
    /// Hashes are excluded from the response — call /verify (Admin) for integrity checks.
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetMyAuditLogs([FromQuery] int limit = 50)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var currentUserId))
        {
            return Unauthorized(new { Message = "Invalid or missing user identity." });
        }

        var cappedLimit = Math.Clamp(limit, 1, 100);

        var logs = await _dbContext.AuditEvents
            .AsNoTracking()
            .Where(a => a.UserId == currentUserId)
            .OrderByDescending(a => a.SequenceNumber)
            .Take(cappedLimit)
            .Select(a => new AuditEventSummaryDto(
                a.Id,
                a.SequenceNumber,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.TimestampUtc,
                a.Details,
                a.Status
            ))
            .ToListAsync();

        return Ok(logs);
    }
}
