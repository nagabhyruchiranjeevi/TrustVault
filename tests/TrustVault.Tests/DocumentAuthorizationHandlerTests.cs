using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using TrustVault.Api.Auth;
using TrustVault.Api.Data;
using TrustVault.Api.Models;
using Xunit;

namespace TrustVault.Tests;

public class DocumentAuthorizationHandlerTests
{
    private readonly ApplicationDbContext _dbContext;
    private readonly Mock<ILogger<DocumentAuthorizationHandler>> _loggerMock;
    private readonly DocumentAuthorizationHandler _handler;

    public DocumentAuthorizationHandlerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _loggerMock = new Mock<ILogger<DocumentAuthorizationHandler>>();
        _handler = new DocumentAuthorizationHandler(_dbContext, _loggerMock.Object);
    }

    private static ClaimsPrincipal CreatePrincipal(Guid userId, string email, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public async Task Owner_Should_Be_Authorized_To_Read_And_Edit_Document()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var principal = CreatePrincipal(ownerId, "owner@trustvault.local", Roles.Owner);
        var document = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Confidential Vault Spec",
            OwnerId = ownerId
        };

        var readContext = new AuthorizationHandlerContext([DocumentOperations.Read], principal, document);
        var editContext = new AuthorizationHandlerContext([DocumentOperations.Edit], principal, document);

        // Act
        await _handler.HandleAsync(readContext);
        await _handler.HandleAsync(editContext);

        // Assert
        readContext.HasSucceeded.Should().BeTrue("the document owner must have Read access");
        editContext.HasSucceeded.Should().BeTrue("the document owner must have Edit access");
    }

    [Fact]
    public async Task Unrelated_User_Without_Permissions_Should_Be_Denied()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var principal = CreatePrincipal(strangerId, "stranger@trustvault.local", Roles.Viewer);
        var document = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Private Report",
            OwnerId = ownerId
        };

        var readContext = new AuthorizationHandlerContext([DocumentOperations.Read], principal, document);

        // Act
        await _handler.HandleAsync(readContext);

        // Assert
        readContext.HasSucceeded.Should().BeFalse("unrelated users without permissions must not read documents");
    }

    [Fact]
    public async Task Admin_Without_Explicit_Permission_Must_NOT_Automatically_Receive_Read_Access()
    {
        // Explicitly tests requirement: "An Admin must not automatically receive permission to read every document."
        // Arrange
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var adminPrincipal = CreatePrincipal(adminId, "admin@trustvault.local", Roles.Admin);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            Title = "CEO Personal Notes",
            OwnerId = ownerId
        };

        var readContext = new AuthorizationHandlerContext([DocumentOperations.Read], adminPrincipal, document);

        // Act
        await _handler.HandleAsync(readContext);

        // Assert
        readContext.HasSucceeded.Should().BeFalse("admin users must not bypass document privacy without explicit permission");
    }

    [Fact]
    public async Task User_With_Explicit_Read_Permission_Can_Read_But_Cannot_Edit()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var viewerPrincipal = CreatePrincipal(viewerId, "viewer@trustvault.local", Roles.Viewer);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Shared Architecture Document",
            OwnerId = ownerId
        };

        var permission = new DocumentPermission
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            UserId = viewerId,
            PermissionLevel = PermissionLevel.Read,
            GrantedById = ownerId
        };

        _dbContext.DocumentPermissions.Add(permission);
        await _dbContext.SaveChangesAsync();

        var readContext = new AuthorizationHandlerContext([DocumentOperations.Read], viewerPrincipal, document);
        var editContext = new AuthorizationHandlerContext([DocumentOperations.Edit], viewerPrincipal, document);

        // Act
        await _handler.HandleAsync(readContext);
        await _handler.HandleAsync(editContext);

        // Assert
        readContext.HasSucceeded.Should().BeTrue("user with Read permission must be granted Read access");
        editContext.HasSucceeded.Should().BeFalse("user with Read-only permission must NOT be granted Edit access");
    }

    [Fact]
    public async Task User_With_Explicit_Edit_Permission_Can_Both_Read_And_Edit()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var editorId = Guid.NewGuid();
        var editorPrincipal = CreatePrincipal(editorId, "editor@trustvault.local", Roles.Owner);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Collaborative Design Proposal",
            OwnerId = ownerId
        };

        var permission = new DocumentPermission
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            UserId = editorId,
            PermissionLevel = PermissionLevel.Edit,
            GrantedById = ownerId
        };

        _dbContext.DocumentPermissions.Add(permission);
        await _dbContext.SaveChangesAsync();

        var readContext = new AuthorizationHandlerContext([DocumentOperations.Read], editorPrincipal, document);
        var editContext = new AuthorizationHandlerContext([DocumentOperations.Edit], editorPrincipal, document);

        // Act
        await _handler.HandleAsync(readContext);
        await _handler.HandleAsync(editContext);

        // Assert
        readContext.HasSucceeded.Should().BeTrue("user with Edit permission can Read");
        editContext.HasSucceeded.Should().BeTrue("user with Edit permission can Edit");
    }
        [Fact]
        public async Task RevokedPermission_Should_Not_Authorize_Read()
        {
            // Arrange - create user, document, grant permission, then revoke
            var ownerId = Guid.NewGuid();
            var viewerId = Guid.NewGuid();
            var principal = CreatePrincipal(viewerId, "viewer@trustvault.local", Roles.Viewer);

            var document = new Document
            {
                Id = Guid.NewGuid(),
                Title = "Secret Doc",
                OwnerId = ownerId
            };

            var permission = new DocumentPermission
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                UserId = viewerId,
                PermissionLevel = PermissionLevel.Read,
                GrantedById = ownerId
            };

            _dbContext.Documents.Add(document);
            _dbContext.DocumentPermissions.Add(permission);
            await _dbContext.SaveChangesAsync();

            // Verify initially authorized
            var initialContext = new AuthorizationHandlerContext([DocumentOperations.Read], principal, document);
            await _handler.HandleAsync(initialContext);
            initialContext.HasSucceeded.Should().BeTrue("permission granted should allow read");

            // Revoke permission (simulate revocation)
            _dbContext.DocumentPermissions.Remove(permission);
            await _dbContext.SaveChangesAsync();

            // New context after revocation
            var afterRevokeContext = new AuthorizationHandlerContext([DocumentOperations.Read], principal, document);
            await _handler.HandleAsync(afterRevokeContext);
            afterRevokeContext.HasSucceeded.Should().BeFalse("revoked permission must deny read immediately");
        }
        }

