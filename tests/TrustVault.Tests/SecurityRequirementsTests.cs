using System.Security.Claims;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TrustVault.Api.Auth;
using TrustVault.Api.Controllers;
using TrustVault.Api.Data;
using TrustVault.Api.DTOs;
using TrustVault.Api.Models;
using TrustVault.Api.Services;
using TrustVault.Api.Validators;
using Xunit;

namespace TrustVault.Tests;

public class SecurityRequirementsTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly Mock<IDocumentStorageService> _storageMock;
    private readonly Mock<IAuditService> _auditMock;
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
    private readonly DocumentAuthorizationHandler _authHandler;
    private readonly DefaultAuthorizationService _authorizationService;
    private readonly DocumentsController _controller;

    public SecurityRequirementsTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _storageMock = new Mock<IDocumentStorageService>();
        _auditMock = new Mock<IAuditService>();

        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _userManagerMock = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        var authHandlerLogger = new Mock<ILogger<DocumentAuthorizationHandler>>();
        _authHandler = new DocumentAuthorizationHandler(_dbContext, authHandlerLogger.Object);

        // Standard Authorization Service configured with our DocumentAuthorizationHandler
        var authOptions = Options.Create(new AuthorizationOptions());
        var handlers = new IAuthorizationHandler[] { _authHandler };
        var handlerProviderMock = new Mock<IAuthorizationHandlerProvider>();
        handlerProviderMock.Setup(h => h.GetHandlersAsync(It.IsAny<AuthorizationHandlerContext>()))
            .ReturnsAsync(handlers);

        var handlerContextFactoryMock = new Mock<IAuthorizationHandlerContextFactory>();
        handlerContextFactoryMock.Setup(f => f.CreateContext(It.IsAny<IEnumerable<IAuthorizationRequirement>>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>()))
            .Returns<IEnumerable<IAuthorizationRequirement>, ClaimsPrincipal, object?>((reqs, user, res) =>
                new AuthorizationHandlerContext(reqs, user, res));

        var authEvaluator = new DefaultAuthorizationEvaluator();
        var policyProvider = new DefaultAuthorizationPolicyProvider(authOptions);

        _authorizationService = new DefaultAuthorizationService(
            policyProvider,
            handlerProviderMock.Object,
            new Mock<ILogger<DefaultAuthorizationService>>().Object,
            handlerContextFactoryMock.Object,
            authEvaluator,
            authOptions);

        _controller = new DocumentsController(
            _dbContext,
            _authorizationService,
            _storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator()
        );
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private void SetUserContext(Guid userId, string email, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext
        {
            User = principal
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task Search_And_List_Must_Never_Leak_Unauthorized_Document_Metadata()
    {
        // Arrange - User A owns private doc, User B owns another
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var docA = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Top Secret Strategy 2026",
            FileName = "secret_strategy.pdf",
            ContentType = "application/pdf",
            StorageKey = "key-a.vault",
            OwnerId = userA,
            CreatedAtUtc = DateTime.UtcNow
        };

        var docB = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Public Marketing Roadmap",
            FileName = "roadmap.pdf",
            ContentType = "application/pdf",
            StorageKey = "key-b.vault",
            OwnerId = userB,
            CreatedAtUtc = DateTime.UtcNow
        };

        var userAEntity = new ApplicationUser { Id = userA, Email = "usera@trustvault.local", FullName = "User A" };
        var userBEntity = new ApplicationUser { Id = userB, Email = "userb@trustvault.local", FullName = "User B" };

        _dbContext.Users.AddRange(userAEntity, userBEntity);
        _dbContext.Documents.AddRange(docA, docB);
        await _dbContext.SaveChangesAsync();

        // Act - User B searches for "Secret"
        SetUserContext(userB, "userb@trustvault.local", Roles.Viewer);
        var searchResult = await _controller.SearchDocuments("Secret") as OkObjectResult;
        var listResult = await _controller.ListDocuments() as OkObjectResult;

        // Assert - User B receives ZERO results from User A's private doc
        searchResult.Should().NotBeNull();
        var searchItems = (searchResult!.Value as IEnumerable<DocumentDto>)?.ToList();
        searchItems.Should().NotBeNull();
        searchItems.Should().BeEmpty("search must not leak titles of unauthorized documents to other users");

        listResult.Should().NotBeNull();
        var listItems = (listResult!.Value as IEnumerable<DocumentDto>)?.ToList();
        listItems.Should().NotBeNull();
        listItems!.Should().HaveCount(1);
        listItems![0].Id.Should().Be(docB.Id);
        listItems.Any(d => d.Id == docA.Id).Should().BeFalse("unauthorized documents must not appear in user document listings");
    }

    [Fact]
    public async Task Soft_Deleted_Document_Must_Immediately_Prevent_Retrieval_And_Download()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Sensitive Financials",
            FileName = "financials.pdf",
            ContentType = "application/pdf",
            StorageKey = "sensitive.vault",
            OwnerId = ownerId,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        SetUserContext(ownerId, "owner@trustvault.local", Roles.Owner);

        // Act 1: Soft delete the document
        var deleteResult = await _controller.DeleteDocument(doc.Id);
        deleteResult.Should().BeOfType<NoContentResult>();

        // Assert: Database marked as deleted
        var dbDoc = await _dbContext.Documents.FirstAsync(d => d.Id == doc.Id);
        dbDoc.IsDeleted.Should().BeTrue();

        // Act 2: Attempt to get metadata or download
        var getResult = await _controller.GetDocument(doc.Id);
        var downloadResult = await _controller.DownloadDocument(doc.Id);

        // Assert: Both return NotFound (404) immediately
        getResult.Should().BeOfType<NotFoundObjectResult>();
        downloadResult.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task StorageKeys_Generated_Must_Be_Decoupled_From_Untrusted_Filenames()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "TrustVaultStorageTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var configMock = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
            configMock.Setup(c => c["Storage:LocalVaultPath"]).Returns(tempDir);

            var envMock = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            envMock.Setup(e => e.ContentRootPath).Returns(tempDir);

            var loggerMock = new Mock<ILogger<LocalSecureFileStorageService>>();
            var storageService = new LocalSecureFileStorageService(configMock.Object, envMock.Object, loggerMock.Object);

            var maliciousFileName = "../../../../etc/passwd";
            var sampleContent = new byte[] { 0x01, 0x02, 0x03, 0x04 };

            // Act
            var key = await storageService.SaveDocumentAsync(sampleContent, maliciousFileName, "application/octet-stream");

            // Assert
            key.Should().NotContain("passwd");
            key.Should().NotContain("..");
            key.Should().MatchRegex("^[a-f0-9]{32}\\.vault$", "keys must be cryptographically unguessable GUID hex strings with .vault suffix");

            var storedBytes = await storageService.GetDocumentBytesAsync(key);
            storedBytes.Should().Equal(sampleContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Unauthorized_User_Cannot_Grant_Or_Revoke_Permissions()
    {
        // Arrange - User A owns doc; User B attempts to grant permission to User C
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var userC = Guid.NewGuid();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Confidential Patent",
            FileName = "patent.pdf",
            ContentType = "application/pdf",
            StorageKey = "patent.vault",
            OwnerId = userA,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        // User B (not owner) attempts to grant permissions
        SetUserContext(userB, "userb@trustvault.local", Roles.Viewer);
        var grantDto = new GrantPermissionDto("userc@trustvault.local", PermissionLevel.Read);

        // Act
        var grantResult = await _controller.GrantPermission(doc.Id, grantDto);
        var revokeResult = await _controller.RevokePermission(doc.Id, userC);

        // Assert
        grantResult.Should().BeOfType<ForbidResult>("non-owners cannot grant document permissions");
        revokeResult.Should().BeOfType<ForbidResult>("non-owners cannot revoke document permissions");
    }

    [Fact]
    public async Task Owner_Cannot_Grant_Permissions_To_Themselves()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var ownerEmail = "owner@trustvault.local";

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Owner Document",
            FileName = "doc.pdf",
            ContentType = "application/pdf",
            StorageKey = "doc.vault",
            OwnerId = ownerId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var ownerUser = new ApplicationUser { Id = ownerId, Email = ownerEmail };
        _userManagerMock.Setup(m => m.FindByEmailAsync(ownerEmail)).ReturnsAsync(ownerUser);

        SetUserContext(ownerId, ownerEmail, Roles.Owner);
        var grantDto = new GrantPermissionDto(ownerEmail, PermissionLevel.Read);

        // Act
        var result = await _controller.GrantPermission(doc.Id, grantDto);

        // Assert
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value?.ToString().Should().Contain("cannot grant permissions to yourself");
    }

    [Fact]
    public async Task Revoking_Permission_Immediately_Denies_Subsequent_Download()
    {
        // Arrange - User A owns document, grants Read to User B
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var userBEmail = "userb@trustvault.local";

        var userAEntity = new ApplicationUser { Id = userA, Email = "ownera@trustvault.local", FullName = "Owner A" };
        var userBEntity = new ApplicationUser { Id = userB, Email = userBEmail, FullName = "User B" };

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Revocation Test Doc",
            FileName = "contract.pdf",
            ContentType = "application/pdf",
            StorageKey = "contract.vault",
            OwnerId = userA,
            CreatedAtUtc = DateTime.UtcNow
        };

        var permission = new DocumentPermission
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            UserId = userB,
            PermissionLevel = PermissionLevel.Read,
            GrantedById = userA,
            GrantedAtUtc = DateTime.UtcNow
        };

        _dbContext.Users.AddRange(userAEntity, userBEntity);
        _dbContext.Documents.Add(doc);
        _dbContext.DocumentPermissions.Add(permission);
        await _dbContext.SaveChangesAsync();

        _userManagerMock.Setup(m => m.FindByEmailAsync(userBEmail)).ReturnsAsync(userBEntity);
        _storageMock.Setup(s => s.GetDocumentStreamAsync(doc.StorageKey, default))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        // Act 1: User B downloads document -> Success
        SetUserContext(userB, userBEmail, Roles.Viewer);
        var initialDownload = await _controller.DownloadDocument(doc.Id);
        initialDownload.Should().BeOfType<FileStreamResult>("User B has active Read permission");

        // Act 2: Owner A revokes User B's permission
        SetUserContext(userA, "ownera@trustvault.local", Roles.Owner);
        var revokeResult = await _controller.RevokePermission(doc.Id, userB);
        revokeResult.Should().BeOfType<OkObjectResult>();

        // Act 3: User B attempts download again immediately
        SetUserContext(userB, userBEmail, Roles.Viewer);
        var postRevocationDownload = await _controller.DownloadDocument(doc.Id);

        // Assert: 403 Forbidden
        postRevocationDownload.Should().BeOfType<ForbidResult>("immediate server-side authorization check must deny download after revocation");
    }
}
