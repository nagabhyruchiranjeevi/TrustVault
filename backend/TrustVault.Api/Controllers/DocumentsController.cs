using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public DocumentsController(
        ApplicationDbContext dbContext,
        IAuthorizationService authorizationService,
        IDocumentStorageService storageService,
        IAuditService auditService,
        UserManager<ApplicationUser> userManager,
        IValidator<CreateDocumentDto> createValidator,
        IValidator<GrantPermissionDto> permissionValidator,
        IValidator<UpdateDocumentDto> updateValidator)
    {
        _dbContext = dbContext;
        _authorizationService = authorizationService;
        _storageService = storageService;
        _auditService = auditService;
        _userManager = userManager;
        _createValidator = createValidator;
        _permissionValidator = permissionValidator;
        _updateValidator = updateValidator;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string CorrelationId => HttpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? HttpContext.TraceIdentifier;

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

        var fileStream = await _storageService.GetDocumentStreamAsync(document.StorageKey);
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
        /// Edits document metadata (currently only Title). Requires DocumentOperations.Edit.
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

        document.IsDeleted = true;
        document.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
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
