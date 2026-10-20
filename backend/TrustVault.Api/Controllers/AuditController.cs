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
[Authorize(Roles = Roles.Admin)]
public class AuditController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public AuditController(ApplicationDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    [HttpGet]
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

    [HttpGet("verify")]
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
}
