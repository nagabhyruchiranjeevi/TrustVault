using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrustVault.Api.Auth;
using TrustVault.Api.Data;
using TrustVault.Api.DTOs;
using TrustVault.Api.Models;
using TrustVault.Api.Services;
using TrustVault.Api.Validators;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuthorizationService _authorizationService;
    private readonly IDocumentStorageService _storageService;
    private readonly IAuditService _auditService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IValidator<CreateDocumentDto> _createValidator;
    private readonly IValidator<GrantPermissionDto> _permissionValidator;
    private readonly IValidator<UpdateDocumentDto> _updateValidator;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        ApplicationDbContext dbContext,
        IAuthorizationService authorizationService,
        IDocumentStorageService storageService,
        IAuditService auditService,
        UserManager<ApplicationUser> userManager,
        IValidator<CreateDocumentDto> createValidator,
        IValidator<GrantPermissionDto> permissionValidator,
        IValidator<UpdateDocumentDto> updateValidator,
        ILogger<DocumentsController>? logger = null)
    {
        _dbContext = dbContext;
        _authorizationService = authorizationService;
        _storageService = storageService;
        _auditService = auditService;
        _userManager = userManager;
        _createValidator = createValidator;
        _permissionValidator = permissionValidator;
        _updateValidator = updateValidator;
        _logger = logger ?? NullLogger<DocumentsController>.Instance;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string CorrelationId => HttpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? HttpContext.TraceIdentifier;

    /// <summary>
    /// Per-document in-process synchronization locks to serialize concurrent migration requests for the same document.
    /// Prevents race conditions during remote upload, verification, and audit deduplication within a server instance.
    /// Distributed deployment note: In multi-instance environments behind a load balancer, an in-process semaphore
    /// coordinates within the host instance. Because TrustVault runs on a single container instance on Render,
    /// this in-process coordination prevents simultaneous execution without requiring distributed lock infrastructure.
    /// If scaled across multiple instances, database-backed coordination (e.g. PostgreSQL advisory locks or row-level locking)
    /// would be required to coordinate across instances.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _documentMigrationLocks = new();

    /// <summary>
    /// Lists only documents that the caller owns or has an explicit active permission to access.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListDocuments()
    {
        var currentUserId = CurrentUserId;

        var docs = await _dbContext.Documents
            .AsNoTracking()
            .Include(d => d.Owner)
            .Include(d => d.Permissions)
            .Where(d => !d.IsDeleted && (d.OwnerId == currentUserId || d.Permissions.Any(p => p.UserId == currentUserId)))
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync();

        var dtos = docs.Select(d => MapToDto(d, currentUserId));
        return Ok(dtos);
    }

    /// <summary>
    /// Searches titles and filenames, returning ONLY documents the user is authorized to access.
    /// Never leaks unauthorized document titles or existence to unauthorized users.
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> SearchDocuments([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await ListDocuments();
        }

        var currentUserId = CurrentUserId;
        var trimmedQuery = query.Trim().ToLower();

        var docs = await _dbContext.Documents
            .AsNoTracking()
            .Include(d => d.Owner)
            .Include(d => d.Permissions)
            .Where(d => !d.IsDeleted &&
                        (d.OwnerId == currentUserId || d.Permissions.Any(p => p.UserId == currentUserId)) &&
                        (d.Title.ToLower().Contains(trimmedQuery) || d.FileName.ToLower().Contains(trimmedQuery)))
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync();

        var dtos = docs.Select(d => MapToDto(d, currentUserId));
        return Ok(dtos);
    }

    /// <summary>
    /// Retrieves document metadata if authorized via DocumentOperations.Read.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDocument(Guid id)
    {
        var document = await _dbContext.Documents
            .Include(d => d.Owner)
            .Include(d => d.Permissions)
            .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);

        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Read);
        if (!authResult.Succeeded)
        {
            await _auditService.LogEventAsync(CurrentUserId, "ReadDocumentDenied", "Document", id.ToString(),
                "Access denied by resource authorization policy", "Forbidden", ClientIp, CorrelationId);
            return Forbid();
        }

        var dto = MapToDto(document, CurrentUserId);
        return Ok(dto);
    }

    /// <summary>
    /// Downloads document binary. Streams from storage only after validating current authorization.
    /// </summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> DownloadDocument(Guid id)
    {
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Read);
        if (!authResult.Succeeded)
        {
            await _auditService.LogEventAsync(CurrentUserId, "DownloadDocumentDenied", "Document", id.ToString(),
                "Download denied by resource authorization policy", "Forbidden", ClientIp, CorrelationId);
            return Forbid();
        }

        Stream fileStream;
        try
        {
            fileStream = await _storageService.GetDocumentStreamAsync(document.StorageKey);
        }
        catch (FileNotFoundException ex)
        {
            await _auditService.LogEventAsync(CurrentUserId, "DownloadDocumentFailed", "Document", id.ToString(),
                $"Document binary missing from storage: {ex.Message}", "NotFound", ClientIp, CorrelationId);
            return NotFound(new { Message = "The requested document file could not be found in storage." });
        }

        await _auditService.LogEventAsync(CurrentUserId, "DownloadDocument", "Document", id.ToString(),
            $"Downloaded {document.FileName}", "Success", ClientIp, CorrelationId);

        return File(fileStream, document.ContentType, document.FileName);
    }

    /// <summary>
    /// Multipart file upload endpoint. Enforces server-side size limits, extension allowlists, and path safety.
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadDocument([FromForm] IFormFile file, [FromForm] string title)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { Message = "A file must be selected for upload." });
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { Message = "Document title is required." });
        }

        if (title.Length > 255)
        {
            return BadRequest(new { Message = "Title cannot exceed 255 characters." });
        }

        // File size validation
        if (file.Length > SecurityConstants.MaxFileSizeBytes)
        {
            return BadRequest(new { Message = $"File size ({file.Length / (1024 * 1024)}MB) exceeds maximum limit of {SecurityConstants.MaxFileSizeBytes / (1024 * 1024)}MB." });
        }

        // Path traversal defense
        var originalFileName = Path.GetFileName(file.FileName);
        if (file.FileName.Contains("..") || file.FileName.Contains('/') || file.FileName.Contains('\\'))
        {
            return BadRequest(new { Message = "File name contains invalid path traversal sequences." });
        }

        // Extension allowlist & blocklist check
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(ext) || !SecurityConstants.PermittedExtensions.Contains(ext))
        {
            return BadRequest(new { Message = "File extension is not permitted. Permitted types: PDF, PNG, JPG, TXT, DOCX, XLSX, CSV, JSON, XML." });
        }

        if (SecurityConstants.BlockedExtensions.Contains(ext))
        {
            return BadRequest(new { Message = "Executable or script files are strictly prohibited." });
        }

        var currentUserId = CurrentUserId;
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

        // Save binary stream to storage provider
        await using var stream = file.OpenReadStream();
        var storageKey = await _storageService.SaveDocumentAsync(stream, originalFileName, contentType);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            Title = title.Trim(),
            FileName = originalFileName,
            ContentType = contentType,
            FileSizeBytes = file.Length,
            StorageKey = storageKey,
            OwnerId = currentUserId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.Documents.Add(document);
        await _dbContext.SaveChangesAsync();

        await _auditService.LogEventAsync(currentUserId, "UploadDocument", "Document", document.Id.ToString(),
            $"Uploaded '{originalFileName}' ({file.Length} bytes)", "Success", ClientIp, CorrelationId);

        var responseDto = MapToDto(document, currentUserId);
        return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, responseDto);
    }

    /// <summary>
    /// One-time, idempotent migration endpoint to migrate a legacy local-storage document to Supabase Storage.
    /// Strictly restricted to the document Owner or explicitly authorized Administrator.
    /// Requires a valid 64-character hexadecimal expectedSha256 and validates uploaded binary size and hash.
    /// Performs upload to vault-docs/{guid}.vault and verifies read-back byte count and SHA-256 before database update.
    /// On retry, validates that the remote object actually exists and is readable before returning AlreadyMigrated=true.
    /// Avoids duplicate audit success logs on retries and logs sanitized structured diagnostics without secret leakage.
    /// </summary>
    [HttpPost("{id:guid}/migrate-legacy")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(SecurityConstants.MaxFileSizeBytes + 1024 * 1024)]
    public async Task<IActionResult> MigrateLegacyDocument(
        Guid id,
        [FromForm] IFormFile file,
        [FromForm] string expectedSha256)
    {
        var currentUserId = CurrentUserId;

        // 1. Validate file presence
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { Message = "A valid document file must be provided for migration." });
        }

        // 2. Validate expectedSha256 is present, exactly 64 hex characters, and parseable
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return BadRequest(new { Message = "expectedSha256 is required for legacy document migration." });
        }

        var normalizedExpectedSha256 = expectedSha256.Trim();
        if (normalizedExpectedSha256.Length != 64 || !normalizedExpectedSha256.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
        {
            return BadRequest(new { Message = "expectedSha256 must be exactly 64 hexadecimal characters." });
        }

        byte[] expectedHashBytes;
        try
        {
            expectedHashBytes = Convert.FromHexString(normalizedExpectedSha256);
        }
        catch (FormatException)
        {
            return BadRequest(new { Message = "expectedSha256 contains invalid hexadecimal characters." });
        }

        var docLock = _documentMigrationLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await docLock.WaitAsync();
        try
        {
            var document = await _dbContext.Documents
                .Include(d => d.Permissions)
                .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);

        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        // 3. Authorization: Restrict migration strictly to Document Owner or Administrator only.
        // Reject unauthorized callers before any storage or file stream operations.
        var isOwner = document.OwnerId == currentUserId;
        var isAdmin = User.IsInRole(Roles.Admin);
        if (!isOwner && !isAdmin)
        {
            await _auditService.LogEventAsync(currentUserId, "MigrateDocumentDenied", "Document", id.ToString(),
                "Storage migration denied (Owner or Administrator required)", "Forbidden", ClientIp, CorrelationId);
            return Forbid();
        }

        // 4. Safe Idempotent Retries:
        // Do not report successful migration merely because database StorageKey is no longer a legacy key.
        // Verify that the expected remote object actually exists and is readable before returning AlreadyMigrated=true.
        if (!S3DocumentStorageService.IsLegacyLocalKey(document.StorageKey))
        {
            bool remoteVerified = false;
            try
            {
                await using var existingStream = await _storageService.GetDocumentStreamAsync(document.StorageKey);
                if (existingStream != null && existingStream.CanRead)
                {
                    using var verifyMs = new MemoryStream();
                    await existingStream.CopyToAsync(verifyMs);
                    if (verifyMs.Length == document.FileSizeBytes)
                    {
                        using var sha256 = SHA256.Create();
                        var remoteHashBytes = sha256.ComputeHash(verifyMs.ToArray());
                        if (CryptographicOperations.FixedTimeEquals(expectedHashBytes, remoteHashBytes))
                        {
                            remoteVerified = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Remote storage verification for already-migrated document {DocumentId} at key {StorageKey} failed with {ErrorCategory}.",
                    document.Id, document.StorageKey, ex.GetType().Name);
            }

            if (!remoteVerified)
            {
                _logger.LogError("Already-migrated document {DocumentId} has remote key {StorageKey}, but remote object could not be verified in storage.",
                    document.Id, document.StorageKey);

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    Message = "Document was previously marked as migrated, but the remote storage object could not be verified or read. Manual reconciliation required."
                });
            }

            // Remote object is confirmed readable and matches size & checksum.
            // Check if MigrateDocumentStorage audit success already exists to prevent duplicate success events.
            var auditExists = await _dbContext.AuditEvents
                .AsNoTracking()
                .AnyAsync(a => a.EntityId == document.Id.ToString() && a.Action == "MigrateDocumentStorage" && a.Status == "Success");

            if (!auditExists)
            {
                try
                {
                    await _auditService.LogEventAsync(currentUserId, "MigrateDocumentStorage", "Document", document.Id.ToString(),
                        $"Migrated document '{document.Title}' ({document.FileName}) to Supabase Storage key '{document.StorageKey}' (SHA-256: {normalizedExpectedSha256.ToLowerInvariant()})",
                        "Success", ClientIp, CorrelationId);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Failed to record recovered migration audit event for document {DocumentId} with error category {ErrorCategory}.",
                        document.Id, auditEx.GetType().Name);
                }
            }

            return Ok(new
            {
                Message = "Document has already been migrated to durable Supabase Storage.",
                DocumentId = document.Id,
                FileName = document.FileName,
                StorageKey = document.StorageKey,
                AlreadyMigrated = true
            });
        }

        // 5. Upload validation
        if (file.Length > SecurityConstants.MaxFileSizeBytes)
        {
            return BadRequest(new { Message = $"File size ({file.Length / (1024 * 1024)}MB) exceeds maximum limit." });
        }

        // Exact size match against registered database metadata
        if (file.Length != document.FileSizeBytes)
        {
            return BadRequest(new
            {
                Message = $"File size mismatch: uploaded binary is {file.Length} bytes, but registered document size is {document.FileSizeBytes} bytes."
            });
        }

        // Validate filename and extension
        var incomingFileName = Path.GetFileName(file.FileName);
        if (!string.Equals(incomingFileName, document.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                Message = $"Uploaded file name '{incomingFileName}' does not match registered document file name '{document.FileName}'."
            });
        }

        var ext = Path.GetExtension(incomingFileName);
        if (SecurityConstants.BlockedExtensions.Contains(ext) || !SecurityConstants.PermittedExtensions.Contains(ext))
        {
            return BadRequest(new { Message = "Unsupported or blocked file type." });
        }

        // 6. Checksum Enforcement: compute SHA-256 and constant-time compare against expectedSha256
        byte[] fileBytes;
        byte[] computedHashBytes;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }

        using (var sha256 = SHA256.Create())
        {
            computedHashBytes = sha256.ComputeHash(fileBytes);
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedHashBytes, computedHashBytes))
        {
            return BadRequest(new
            {
                Message = "Binary SHA-256 checksum mismatch: uploaded file content does not match expectedSha256."
            });
        }

        var hexUploadedHash = Convert.ToHexString(computedHashBytes).ToLowerInvariant();
        var oldStorageKey = document.StorageKey;
        string newStorageKey;

        // 7. Upload binary to Supabase Storage under exact object-key convention (vault-docs/{guid}.vault)
        try
        {
            using var uploadStream = new MemoryStream(fileBytes);
            newStorageKey = await _storageService.SaveDocumentAsync(uploadStream, document.FileName, document.ContentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed uploading binary to remote storage for document {DocumentId}. Error: {ErrorCategory}", id, ex.GetType().Name);
            await _auditService.LogEventAsync(currentUserId, "MigrateDocumentFailed", "Document", id.ToString(),
                $"Failed uploading binary to remote storage: {ex.GetType().Name}", "Failure", ClientIp, CorrelationId);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Message = "Failed to upload document binary to Supabase Storage. Database remains unchanged; safe to retry."
            });
        }

        // 8. Remote Read-Back Verification: read stream back from remote storage, verify byte count and SHA-256
        bool readBackVerified = false;
        try
        {
            await using var verifyStream = await _storageService.GetDocumentStreamAsync(newStorageKey);
            if (verifyStream != null && verifyStream.CanRead)
            {
                using var verifyMs = new MemoryStream();
                await verifyStream.CopyToAsync(verifyMs);
                var verifyBytes = verifyMs.ToArray();

                if (verifyBytes.Length == fileBytes.Length)
                {
                    using var sha256Verify = SHA256.Create();
                    var verifyHashBytes = sha256Verify.ComputeHash(verifyBytes);
                    if (CryptographicOperations.FixedTimeEquals(computedHashBytes, verifyHashBytes))
                    {
                        readBackVerified = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote read-back verification exception for document {DocumentId} at key {StorageKey}. Error: {ErrorCategory}", id, newStorageKey, ex.GetType().Name);
        }

        if (!readBackVerified)
        {
            _logger.LogError("Remote read-back verification failed for document {DocumentId} at key {StorageKey}. Content or checksum mismatch.", id, newStorageKey);
            await _auditService.LogEventAsync(currentUserId, "MigrateDocumentVerificationFailed", "Document", id.ToString(),
                $"Remote read-back verification failed for key '{newStorageKey}'. Content or checksum mismatch.", "Failure", ClientIp, CorrelationId);

            // Attempt cleanup without silently swallowing failure
            try
            {
                await _storageService.DeleteDocumentAsync(newStorageKey);
                _logger.LogInformation("Cleaned up unverified remote object at key {StorageKey}.", newStorageKey);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Failed to clean up unverified remote storage object at key {StorageKey} for document {DocumentId}. Error: {ErrorCategory}", newStorageKey, document.Id, cleanupEx.GetType().Name);

                try
                {
                    await _auditService.LogEventAsync(currentUserId, "OrphanedStorageObjectWarning", "Document", id.ToString(),
                        $"Remote storage verification failed and automated cleanup failed for key '{newStorageKey}': {cleanupEx.GetType().Name}. Manual reconciliation required.",
                        "Warning", ClientIp, CorrelationId);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Failed to record audit event for orphaned storage key {StorageKey}. Error: {ErrorCategory}", newStorageKey, auditEx.GetType().Name);
                }
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Message = "Remote storage verification failed after upload. Remote object cleanup attempted; database remains unchanged; safe to retry."
            });
        }

        // 9. Update database StorageKey and UpdatedAtUtc
        try
        {
            document.StorageKey = newStorageKey;
            document.UpdatedAtUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx, "Database update failed for document {DocumentId} after remote storage key {StorageKey} was verified. Error: {ErrorCategory}", id, newStorageKey, dbEx.GetType().Name);

            // Remote object is in storage but DB update failed; clean up remote object so retries start fresh
            try
            {
                await _storageService.DeleteDocumentAsync(newStorageKey);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Failed to clean up remote storage object at key {StorageKey} after DB update failure. Error: {ErrorCategory}", newStorageKey, cleanupEx.GetType().Name);
                try
                {
                    await _auditService.LogEventAsync(currentUserId, "OrphanedStorageObjectWarning", "Document", id.ToString(),
                        $"Database save failed and automated cleanup failed for key '{newStorageKey}': {cleanupEx.GetType().Name}.",
                        "Warning", ClientIp, CorrelationId);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Failed to record audit event for orphaned storage key {StorageKey}. Error: {ErrorCategory}", newStorageKey, auditEx.GetType().Name);
                }
            }

            await _auditService.LogEventAsync(currentUserId, "MigrateDocumentDatabaseFailed", "Document", id.ToString(),
                $"Database StorageKey update failed: {dbEx.GetType().Name}. Remote object cleaned up; safe to retry.",
                "Failure", ClientIp, CorrelationId);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Message = "Database update failed while updating document storage key. Safe to retry."
            });
        }

        // 10. Audit successful migration; preserve audit hash chain integrity
        try
        {
            var auditAlreadyExists = await _dbContext.AuditEvents
                .AsNoTracking()
                .AnyAsync(a => a.EntityId == document.Id.ToString() && a.Action == "MigrateDocumentStorage" && a.Status == "Success");

            if (!auditAlreadyExists)
            {
                await _auditService.LogEventAsync(currentUserId, "MigrateDocumentStorage", "Document", document.Id.ToString(),
                    $"Successfully migrated document '{document.Title}' ({document.FileName}) from legacy local key '{oldStorageKey}' to Supabase Storage key '{newStorageKey}' (SHA-256: {hexUploadedHash})",
                    "Success", ClientIp, CorrelationId);
            }
        }
        catch (Exception auditEx)
        {
            _logger.LogError(auditEx, "Document {DocumentId} storage key updated to {StorageKey}, but audit event logging failed with {ErrorCategory}. Retry will complete audit trail idempotently.", document.Id, newStorageKey, auditEx.GetType().Name);
        }

        return Ok(new
        {
            Message = "Document successfully migrated and verified in Supabase Storage.",
            DocumentId = document.Id,
            FileName = document.FileName,
            Sha256 = hexUploadedHash,
            PreviousStorageKey = oldStorageKey,
            StorageKey = newStorageKey,
            Migrated = true
        });
        }
        finally
        {
            docLock.Release();
        }
    }

    /// <summary>
    /// Base64 JSON upload endpoint for programmatic clients.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateDocument([FromBody] CreateDocumentDto dto)
    {
        var validation = await _createValidator.ValidateAsync(dto);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        byte[] binaryContent;
        try
        {
            binaryContent = Convert.FromBase64String(dto.Base64Content);
        }
        catch (FormatException)
        {
            return BadRequest(new { Message = "Invalid base64 payload provided." });
        }

        if (binaryContent.Length > SecurityConstants.MaxFileSizeBytes)
        {
            return BadRequest(new { Message = $"File size ({binaryContent.Length / (1024 * 1024)}MB) exceeds maximum limit of {SecurityConstants.MaxFileSizeBytes / (1024 * 1024)}MB." });
        }

        var storageKey = await _storageService.SaveDocumentAsync(binaryContent, dto.FileName, dto.ContentType);
        var currentUserId = CurrentUserId;

        var document = new Document
        {
            Id = Guid.NewGuid(),
            Title = dto.Title.Trim(),
            FileName = dto.FileName,
            ContentType = string.IsNullOrWhiteSpace(dto.ContentType) ? "application/octet-stream" : dto.ContentType,
            FileSizeBytes = binaryContent.Length,
            StorageKey = storageKey,
            OwnerId = currentUserId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.Documents.Add(document);
        await _dbContext.SaveChangesAsync();

        await _auditService.LogEventAsync(currentUserId, "CreateDocument", "Document", document.Id.ToString(),
            $"Uploaded {document.FileName}", "Success", ClientIp, CorrelationId);

        var responseDto = MapToDto(document, currentUserId);
        return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, responseDto);
    }

    /// <summary>
    /// Supported metadata editing (updates Title). Requires DocumentOperations.Edit.
    /// Viewers CANNOT edit.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateDocument(Guid id, [FromBody] UpdateDocumentDto dto)
    {
        var validation = await _updateValidator.ValidateAsync(dto);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Edit);
        if (!authResult.Succeeded)
        {
            await _auditService.LogEventAsync(CurrentUserId, "UpdateDocumentDenied", "Document", id.ToString(),
                "Edit permission denied", "Forbidden", ClientIp, CorrelationId);
            return Forbid();
        }

        var oldTitle = document.Title;
        document.Title = dto.Title.Trim();
        document.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        await _auditService.LogEventAsync(CurrentUserId, "UpdateDocument", "Document", id.ToString(),
            $"Updated title from '{oldTitle}' to '{document.Title}'", "Success", ClientIp, CorrelationId);

        return Ok(new { Message = "Document updated successfully." });
    }

    /// <summary>
    /// Edits document metadata (title). Requires DocumentOperations.Edit.
    /// Viewers cannot edit. Both owners and users with Edit permission may update.
    /// </summary>
    [HttpPatch("{id:guid}/metadata")]
    public async Task<IActionResult> EditMetadata(Guid id, [FromBody] UpdateDocumentDto dto)
    {
        var validation = await _updateValidator.ValidateAsync(dto);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors);

        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (document == null)
            return NotFound(new { Message = "Document not found." });

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Edit);
        if (!authResult.Succeeded)
        {
            await _auditService.LogEventAsync(CurrentUserId, "EditMetadataDenied", "Document", id.ToString(),
                "Edit permission denied", "Forbidden", ClientIp, CorrelationId);
            return Forbid();
        }

        var oldTitle = document.Title;
        document.Title = dto.Title.Trim();
        document.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        await _auditService.LogEventAsync(CurrentUserId, "EditMetadata", "Document", id.ToString(),
            $"Metadata updated: title from '{oldTitle}' to '{document.Title}'", "Success", ClientIp, CorrelationId);

        return Ok(new { Message = "Document metadata updated successfully." });
    }


    /// <summary>
    /// Soft deletes document. Requires DocumentOperations.Delete (Owner only).
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteDocument(Guid id)
    {
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Delete);
        if (!authResult.Succeeded)
        {
            await _auditService.LogEventAsync(CurrentUserId, "DeleteDocumentDenied", "Document", id.ToString(),
                "Delete permission denied (Owner required)", "Forbidden", ClientIp, CorrelationId);
            return Forbid();
        }

        // Delete from storage first. If storage delete fails, abort to avoid partial deletion state.
        try
        {
            await _storageService.DeleteDocumentAsync(document.StorageKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storage deletion failed for document {DocumentId}. Error: {ErrorCategory}", id, ex.GetType().Name);
            await _auditService.LogEventAsync(CurrentUserId, "DeleteDocumentFailed", "Document", id.ToString(),
                $"Storage deletion failed for '{document.Title}': {ex.GetType().Name}", "Failure", ClientIp, CorrelationId);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Message = "Failed to completely delete document binary from storage. Deletion aborted."
            });
        }

        try
        {
            document.IsDeleted = true;
            document.UpdatedAtUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx, "Database soft-delete update failed for document {DocumentId}. Error: {ErrorCategory}", id, dbEx.GetType().Name);
            await _auditService.LogEventAsync(CurrentUserId, "DeleteDocumentStateMismatch", "Document", id.ToString(),
                $"Storage binary was deleted but database update failed for '{document.Title}': {dbEx.GetType().Name}. Retrying delete operation will synchronize state.",
                "Failure", ClientIp, CorrelationId);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Message = "Document binary was removed from storage, but updating document status in the database failed. Please retry the delete request to synchronize state."
            });
        }

        await _auditService.LogEventAsync(CurrentUserId, "DeleteDocument", "Document", id.ToString(),
            $"Document '{document.Title}' ({document.FileName}) soft-deleted", "Success", ClientIp, CorrelationId);

        return NoContent();
    }

    /// <summary>
    /// Grants permission to another user. Requires DocumentOperations.Share (Owner only).
    /// Users cannot grant permissions to themselves.
    /// </summary>
    [HttpPost("{id:guid}/permissions")]
    public async Task<IActionResult> GrantPermission(Guid id, [FromBody] GrantPermissionDto dto)
    {
        var validation = await _permissionValidator.ValidateAsync(dto);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Share);
        if (!authResult.Succeeded)
        {
            await _auditService.LogEventAsync(CurrentUserId, "ShareDocumentDenied", "Document", id.ToString(),
                "Only document owner can grant permissions", "Forbidden", ClientIp, CorrelationId);
            return Forbid();
        }

        var targetUser = await _userManager.FindByEmailAsync(dto.UserEmail.Trim());
        if (targetUser == null)
        {
            return NotFound(new { Message = $"Target user with email '{dto.UserEmail}' not found." });
        }

        // Security check: users cannot grant permissions to themselves
        if (targetUser.Id == CurrentUserId)
        {
            return BadRequest(new { Message = "You cannot grant permissions to yourself." });
        }

        // Security check: document owner already has full rights
        if (targetUser.Id == document.OwnerId)
        {
            return BadRequest(new { Message = "Document owner already has full rights." });
        }

        var existingPerm = await _dbContext.DocumentPermissions
            .FirstOrDefaultAsync(p => p.DocumentId == id && p.UserId == targetUser.Id);

        if (existingPerm != null)
        {
            existingPerm.PermissionLevel = dto.PermissionLevel;
            existingPerm.GrantedAtUtc = DateTime.UtcNow;
            existingPerm.GrantedById = CurrentUserId;
        }
        else
        {
            var newPerm = new DocumentPermission
            {
                Id = Guid.NewGuid(),
                DocumentId = id,
                UserId = targetUser.Id,
                PermissionLevel = dto.PermissionLevel,
                GrantedAtUtc = DateTime.UtcNow,
                GrantedById = CurrentUserId
            };
            _dbContext.DocumentPermissions.Add(newPerm);
        }

        await _dbContext.SaveChangesAsync();
        await _auditService.LogEventAsync(CurrentUserId, "GrantPermission", "DocumentPermission", id.ToString(),
            $"Granted {dto.PermissionLevel} to {dto.UserEmail}", "Success", ClientIp, CorrelationId);

        return Ok(new { Message = $"Permission '{dto.PermissionLevel}' granted to {dto.UserEmail} successfully." });
    }

    /// <summary>
    /// Revokes permission immediately for a named user. Requires DocumentOperations.Share (Owner only).
    /// Once revoked, any subsequent request from the target user is rejected immediately.
    /// </summary>
    [HttpDelete("{id:guid}/permissions/{targetUserId:guid}")]
    public async Task<IActionResult> RevokePermission(Guid id, Guid targetUserId)
    {
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Share);
        if (!authResult.Succeeded)
        {
            await _auditService.LogEventAsync(CurrentUserId, "RevokePermissionDenied", "Document", id.ToString(),
                "Only document owner can revoke permissions", "Forbidden", ClientIp, CorrelationId);
            return Forbid();
        }

        var perm = await _dbContext.DocumentPermissions
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.DocumentId == id && p.UserId == targetUserId);

        if (perm == null)
        {
            return NotFound(new { Message = "Permission record not found for this user." });
        }

        var targetEmail = perm.User?.Email ?? targetUserId.ToString();
        _dbContext.DocumentPermissions.Remove(perm);
        await _dbContext.SaveChangesAsync();

        await _auditService.LogEventAsync(CurrentUserId, "RevokePermission", "DocumentPermission", id.ToString(),
            $"Revoked permission for user {targetEmail}", "Success", ClientIp, CorrelationId);

        return Ok(new { Message = $"Access revoked for user {targetEmail} immediately." });
    }

    /// <summary>
    /// Lists active permissions for a document. Requires DocumentOperations.Share (Owner only).
    /// </summary>
    [HttpGet("{id:guid}/permissions")]
    public async Task<IActionResult> GetPermissions(Guid id)
    {
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Share);
        if (!authResult.Succeeded)
        {
            return Forbid();
        }

        var perms = await _dbContext.DocumentPermissions
            .Include(p => p.User)
            .Where(p => p.DocumentId == id)
            .Select(p => new DocumentPermissionDto(
                p.Id,
                p.DocumentId,
                p.UserId,
                p.User != null ? p.User.Email! : "Unknown",
                p.PermissionLevel.ToString(),
                p.GrantedAtUtc
            ))
            .ToListAsync();

        return Ok(perms);
    }

    private DocumentDto MapToDto(Document doc, Guid currentUserId)
    {
        var isOwner = doc.OwnerId == currentUserId;
        var perm = doc.Permissions?.FirstOrDefault(p => p.UserId == currentUserId);
        var effectivePerm = isOwner ? "Owner" : perm?.PermissionLevel.ToString() ?? "None";

        return new DocumentDto(
            doc.Id,
            doc.Title,
            doc.FileName,
            doc.ContentType,
            doc.FileSizeBytes,
            doc.OwnerId,
            doc.Owner?.Email,
            doc.CreatedAtUtc,
            doc.UpdatedAtUtc,
            isOwner,
            effectivePerm
        );
    }
}
