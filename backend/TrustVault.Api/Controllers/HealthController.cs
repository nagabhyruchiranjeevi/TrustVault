using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Data;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _env;

    public HealthController(ApplicationDbContext dbContext, IWebHostEnvironment env)
    {
        _dbContext = dbContext;
        _env = env;
    }

    [HttpGet]
    public async Task<IActionResult> GetHealth()
    {
        var dbHealthy = false;
        string? dbError = null;

        try
        {
            dbHealthy = await _dbContext.Database.CanConnectAsync();
        }
        catch (Exception ex)
        {
            dbError = ex.Message;
        }

        var status = dbHealthy ? "Healthy" : "Degraded";
        var statusCode = dbHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;

        return StatusCode(statusCode, new
        {
            Status = status,
            Environment = _env.EnvironmentName,
            TimestampUtc = DateTime.UtcNow,
            Version = "1.0.0",
            Checks = new
            {
                Database = new
                {
                    Status = dbHealthy ? "Healthy" : "Unhealthy",
                    Error = _env.IsDevelopment() ? dbError : (dbHealthy ? null : "Database connection failed")
                }
            }
        });
    }
}
