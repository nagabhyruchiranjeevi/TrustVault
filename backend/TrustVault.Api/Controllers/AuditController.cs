using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Data;
using TrustVault.Api.DTOs;
using TrustVault.Api.Models;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Admin)]
public class AuditController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;

    public AuditController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetAuditLogs([FromQuery] int limit = 50)
    {
        var cappedLimit = Math.Clamp(limit, 1, 200);

        var logs = await _dbContext.AuditEvents
            .AsNoTracking()
            .Include(a => a.User)
            .OrderByDescending(a => a.TimestampUtc)
            .Take(cappedLimit)
            .Select(a => new AuditEventDto(
                a.Id,
                a.UserId,
                a.User != null ? a.User.Email : null,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.TimestampUtc,
                a.IpAddress,
                a.Details,
                a.Status
            ))
            .ToListAsync();

        return Ok(logs);
    }
}
