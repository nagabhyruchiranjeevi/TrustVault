using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Data;
using TrustVault.Api.Models;

namespace TrustVault.Api.Auth;

/// <summary>
/// Resource-based authorization handler for Document entities.
/// Strictly enforces the rule that Admins DO NOT automatically receive permission to read or modify documents.
/// Access is granted exclusively to Document Owners and users explicitly granted DocumentPermissions.
/// </summary>
public class DocumentAuthorizationHandler : AuthorizationHandler<OperationAuthorizationRequirement, Document>
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<DocumentAuthorizationHandler> _logger;

    public DocumentAuthorizationHandler(ApplicationDbContext dbContext, ILogger<DocumentAuthorizationHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement,
        Document resource)
    {
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            _logger.LogWarning("Authorization failed: Missing or invalid user identifier claim.");
            return;
        }

        // 1. Document Owner has full access to their documents
        if (resource.OwnerId == userId)
        {
            context.Succeed(requirement);
            return;
        }

        // 2. Security Check: Admin role check.
        // As explicitly required: "An Admin must not automatically receive permission to read every document."
        // We explicitly log and prevent automatic privilege escalation for Admins.
        var isAdmin = context.User.IsInRole(Roles.Admin);
        if (isAdmin)
        {
            _logger.LogInformation("Admin user {UserId} attempting document operation {Operation} on document {DocId}. Admins do not receive automatic document access.",
                userId, requirement.Name, resource.Id);
        }

        // 3. Check explicit DocumentPermissions
        var permission = await _dbContext.DocumentPermissions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.DocumentId == resource.Id && p.UserId == userId);

        if (permission == null)
        {
            // No permission granted
            return;
        }

        if (requirement.Name == DocumentOperations.Read.Name)
        {
            // Both Read and Edit permission levels permit reading
            if (permission.PermissionLevel == PermissionLevel.Read || permission.PermissionLevel == PermissionLevel.Edit)
            {
                context.Succeed(requirement);
            }
        }
        else if (requirement.Name == DocumentOperations.Edit.Name)
        {
            // Only Edit permission level permits modifying the document
            if (permission.PermissionLevel == PermissionLevel.Edit)
            {
                context.Succeed(requirement);
            }
        }
    }
}
