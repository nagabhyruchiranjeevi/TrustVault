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

    public DocumentsController(
        ApplicationDbContext dbContext,
        IAuthorizationService authorizationService,
        IDocumentStorageService storageService,
        IAuditService auditService,
        UserManager<ApplicationUser> userManager,
        IValidator<CreateDocumentDto> createValidator,
        IValidator<GrantPermissionDto> permissionValidator)
    {
        _dbContext = dbContext;
        _authorizationService = authorizationService;
        _storageService = storageService;
        _auditService = auditService;
        _userManager = userManager;
        _createValidator = createValidator;
        _permissionValidator = permissionValidator;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpGet]
    public async Task<IActionResult> ListDocuments()
    {
        var currentUserId = CurrentUserId;

        // Retrieve documents where the caller is either the owner OR has an active permission
        var docs = await _dbContext.Documents
            .AsNoTracking()
            .Include(d => d.Owner)
            .Include(d => d.Permissions)
            .Where(d => !d.IsDeleted && (d.OwnerId == currentUserId || d.Permissions.Any(p => p.UserId == currentUserId)))
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync();

        var dtos = docs.Select(d =>
        {
            var isOwner = d.OwnerId == currentUserId;
            var perm = d.Permissions.FirstOrDefault(p => p.UserId == currentUserId);
            var effectivePerm = isOwner ? "Owner" : perm?.PermissionLevel.ToString() ?? "None";

            return new DocumentDto(
                d.Id,
                d.Title,
                d.FileName,
                d.ContentType,
                d.FileSizeBytes,
                d.OwnerId,
                d.Owner?.Email,
                d.CreatedAtUtc,
                d.UpdatedAtUtc,
                isOwner,
                effectivePerm
            );
        });

        return Ok(dtos);
    }

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
            await _auditService.LogEventAsync(CurrentUserId, "ReadDocumentDenied", "Document", id.ToString(), "Access denied by resource authorization policy", "Forbidden");
            return Forbid();
        }

        var isOwner = document.OwnerId == CurrentUserId;
        var perm = await _dbContext.DocumentPermissions
            .FirstOrDefaultAsync(p => p.DocumentId == id && p.UserId == CurrentUserId);

        var dto = new DocumentDto(
            document.Id,
            document.Title,
            document.FileName,
            document.ContentType,
            document.FileSizeBytes,
            document.OwnerId,
            document.Owner?.Email,
            document.CreatedAtUtc,
            document.UpdatedAtUtc,
            isOwner,
            isOwner ? "Owner" : perm?.PermissionLevel.ToString() ?? "None"
        );

        return Ok(dto);
    }

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
            await _auditService.LogEventAsync(CurrentUserId, "DownloadDocumentDenied", "Document", id.ToString(), "Access denied", "Forbidden");
            return Forbid();
        }

        var fileBytes = await _storageService.GetDocumentBytesAsync(document.StorageKey);
        await _auditService.LogEventAsync(CurrentUserId, "DownloadDocument", "Document", id.ToString(), $"Downloaded {document.FileName}", "Success");

        return File(fileBytes, document.ContentType, document.FileName);
    }

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

        var storageKey = await _storageService.SaveDocumentAsync(binaryContent, dto.FileName);
        var currentUserId = CurrentUserId;

        var document = new Document
        {
            Id = Guid.NewGuid(),
            Title = dto.Title,
            FileName = dto.FileName,
            ContentType = string.IsNullOrWhiteSpace(dto.ContentType) ? "application/octet-stream" : dto.ContentType,
            FileSizeBytes = binaryContent.Length,
            StorageKey = storageKey,
            OwnerId = currentUserId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.Documents.Add(document);
        await _dbContext.SaveChangesAsync();

        await _auditService.LogEventAsync(currentUserId, "CreateDocument", "Document", document.Id.ToString(), $"Uploaded {document.FileName}", "Success");

        var responseDto = new DocumentDto(
            document.Id,
            document.Title,
            document.FileName,
            document.ContentType,
            document.FileSizeBytes,
            document.OwnerId,
            User.FindFirst(ClaimTypes.Email)?.Value,
            document.CreatedAtUtc,
            document.UpdatedAtUtc,
            true,
            "Owner"
        );

        return CreatedAtAction(nameof(GetDocument), new { id = document.Id }, responseDto);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateDocument(Guid id, [FromBody] UpdateDocumentDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            return BadRequest(new { Message = "Title cannot be empty." });
        }

        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (document == null)
        {
            return NotFound(new { Message = "Document not found." });
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, DocumentOperations.Edit);
        if (!authResult.Succeeded)
        {
            await _auditService.LogEventAsync(CurrentUserId, "UpdateDocumentDenied", "Document", id.ToString(), "Edit permission denied", "Forbidden");
            return Forbid();
        }

        document.Title = dto.Title;
        document.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        await _auditService.LogEventAsync(CurrentUserId, "UpdateDocument", "Document", id.ToString(), $"Updated title to '{dto.Title}'", "Success");

        return Ok(new { Message = "Document updated successfully." });
    }

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
            await _auditService.LogEventAsync(CurrentUserId, "DeleteDocumentDenied", "Document", id.ToString(), "Delete permission denied", "Forbidden");
            return Forbid();
        }

        document.IsDeleted = true;
        document.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        await _auditService.LogEventAsync(CurrentUserId, "DeleteDocument", "Document", id.ToString(), "Document soft-deleted", "Success");

        return NoContent();
    }

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
            await _auditService.LogEventAsync(CurrentUserId, "ShareDocumentDenied", "Document", id.ToString(), "Only document owner can grant permissions", "Forbidden");
            return Forbid();
        }

        var targetUser = await _userManager.FindByEmailAsync(dto.UserEmail);
        if (targetUser == null)
        {
            return NotFound(new { Message = $"Target user with email '{dto.UserEmail}' not found." });
        }

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
            $"Granted {dto.PermissionLevel} to {dto.UserEmail}", "Success");

        return Ok(new { Message = $"Permission '{dto.PermissionLevel}' granted to {dto.UserEmail} successfully." });
    }

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
                p.User!.Email!,
                p.PermissionLevel.ToString(),
                p.GrantedAtUtc
            ))
            .ToListAsync();

        return Ok(perms);
    }
}
