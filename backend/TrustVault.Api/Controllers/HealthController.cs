using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Auth;
using TrustVault.Api.Data;
using TrustVault.Api.Models;
using TrustVault.Api.Services;

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

    /// <summary>
    /// Temporary, administrator-only synthetic storage connectivity probe.
    /// Performs an isolated upload -> read-back -> exact byte comparison -> deletion -> deletion confirmation
    /// cycle against the configured Supabase S3 bucket.
    /// Does not create or modify any document database records or access legacy documents.
    /// Automatically cleans up the synthetic object in a finally block and verifies its removal.
    /// </summary>
    [HttpPost("storage-probe")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ExecuteStorageDiagnosticProbe(
        [FromServices] IDocumentStorageService storageService,
        CancellationToken cancellationToken)
    {
        string? probeKey = null;
        var testPayload = "probe-ping-2026"u8.ToArray();

        bool uploadSucceeded = false;
        bool readBackSucceeded = false;
        bool contentVerified = false;
        bool deleteSucceeded = false;
        bool deleteConfirmed = false;
        string? failureStage = null;
        string? errorCategory = null;

        try
        {
            // 1. Upload tiny synthetic payload via IDocumentStorageService.
            // S3DocumentStorageService.SaveDocumentAsync generates and returns an isolated safe key under vault-docs/{guid}.vault.
            using (var uploadStream = new MemoryStream(testPayload))
            {
                probeKey = await storageService.SaveDocumentAsync(
                    uploadStream,
                    "probe-synthetic.tmp",
                    "application/octet-stream",
                    cancellationToken);
                uploadSucceeded = !string.IsNullOrWhiteSpace(probeKey);
            }

            // 2. Read back object from exact storage key returned by upload
            await using (var readStream = await storageService.GetDocumentStreamAsync(probeKey, cancellationToken))
            {
                readBackSucceeded = readStream != null && readStream.CanRead;
                if (readBackSucceeded)
                {
                    using var ms = new MemoryStream();
                    await readStream!.CopyToAsync(ms, cancellationToken);
                    var readBytes = ms.ToArray();

                    // 3. Cryptographically constant-time verify exact byte content
                    if (CryptographicOperations.FixedTimeEquals(testPayload, readBytes))
                    {
                        contentVerified = true;
                    }
                    else
                    {
                        failureStage = "ContentVerificationMismatch";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failureStage ??= !uploadSucceeded ? "UploadFailed" : (!readBackSucceeded ? "ReadBackFailed" : "ReadBackProcessingFailed");
            errorCategory = ex.GetType().Name;
        }
        finally
        {
            // 4. Reliable cleanup in finally block: always attempt cleanup if an object was created
            if (!string.IsNullOrWhiteSpace(probeKey))
            {
                try
                {
                    await storageService.DeleteDocumentAsync(probeKey, CancellationToken.None);
                    deleteSucceeded = true;

                    // 5. Confirm deletion by verifying the storage provider reports the object as missing.
                    // For S3DocumentStorageService, requesting a deleted vault-docs/* key throws FileNotFoundException.
                    try
                    {
                        await using var verifyMissingStream = await storageService.GetDocumentStreamAsync(probeKey, CancellationToken.None);
                        // If it returned without throwing, the object still exists
                        deleteConfirmed = false;
                    }
                    catch (FileNotFoundException)
                    {
                        deleteConfirmed = true; // Confirmed missing from remote storage
                    }
                }
                catch (Exception cleanupEx)
                {
                    deleteSucceeded = false;
                    deleteConfirmed = false;
                    errorCategory = errorCategory != null
                        ? $"{errorCategory};CleanupFailed:{cleanupEx.GetType().Name}"
                        : $"CleanupFailed:{cleanupEx.GetType().Name}";
                }
            }
        }

        var isHealthy = uploadSucceeded && readBackSucceeded && contentVerified && deleteSucceeded && deleteConfirmed;

        return StatusCode(isHealthy ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway, new
        {
            Status = isHealthy ? "Verified" : "Failed",
            ProbeKey = probeKey,
            Upload = uploadSucceeded ? "Success" : "Failed",
            ReadBack = readBackSucceeded ? "Success" : "Failed",
            ContentVerified = contentVerified,
            Deletion = deleteSucceeded ? "Success" : "Failed",
            DeletionConfirmed = deleteConfirmed,
            FailureStage = failureStage,
            ErrorCategory = errorCategory
        });
    }
}
