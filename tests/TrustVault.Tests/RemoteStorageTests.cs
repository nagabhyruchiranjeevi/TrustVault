using System.Net;
using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

public class RemoteStorageTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly Mock<IAmazonS3> _s3Mock;
    private readonly Mock<IWebHostEnvironment> _envMock;
    private readonly Mock<ILogger<S3DocumentStorageService>> _storageLoggerMock;
    private readonly Mock<IAuditService> _auditMock;
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
    private readonly string _tempLocalDir;

    public RemoteStorageTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "RemoteStorageTest_" + Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _s3Mock = new Mock<IAmazonS3>();
        _envMock = new Mock<IWebHostEnvironment>();
        _storageLoggerMock = new Mock<ILogger<S3DocumentStorageService>>();
        _auditMock = new Mock<IAuditService>();

        _tempLocalDir = Path.Combine(Path.GetTempPath(), "TrustVault_LegacyStorage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempLocalDir);
        _envMock.Setup(e => e.ContentRootPath).Returns(_tempLocalDir);

        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _userManagerMock = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (Directory.Exists(_tempLocalDir))
        {
            try { Directory.Delete(_tempLocalDir, true); } catch { /* ignore */ }
        }
    }

    // ──────────────────────────────────────────────────────────
    // 1. Storage Configuration Validation Tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ValidateStorageConfiguration_ValidSupabaseS3Config_Succeeds()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3BucketName"] = "vault-documents",
            ["Storage:S3ServiceUrl"] = "https://glstabcxyz.supabase.co/storage/v1/s3",
            ["Storage:S3Region"] = "us-east-1",
            ["Storage:S3AccessKey"] = "supabase-s3-access-key-id",
            ["Storage:S3SecretKey"] = "supabase-s3-secret-key-secret"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateStorageConfiguration_S3_MissingBucket_ThrowsSanitizedException()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3AccessKey"] = "access-key",
            ["Storage:S3SecretKey"] = "secret-key"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Storage:S3BucketName'*must be configured*");
    }

    [Fact]
    public void ValidateStorageConfiguration_S3_MissingCredentials_ThrowsSanitizedExceptionWithoutLeakingSecrets()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3BucketName"] = "my-bucket",
            ["Storage:S3ServiceUrl"] = "https://glstabcxyz.supabase.co/storage/v1/s3",
            ["Storage:S3Region"] = "us-east-1",
            ["Storage:S3AccessKey"] = "my-access-key"
            // Missing SecretKey
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: true);

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().Contain("credentials are missing");
        ex.Message.Should().NotContain("my-access-key");
    }

    [Fact]
    public void ValidateStorageConfiguration_S3_MissingServiceUrl_ThrowsSanitizedException()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3BucketName"] = "my-bucket",
            ["Storage:S3Region"] = "us-east-1",
            ["Storage:S3AccessKey"] = "access-key",
            ["Storage:S3SecretKey"] = "secret-key"
            // Missing Storage:S3ServiceUrl
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Storage:S3ServiceUrl'*must be configured*");
    }

    [Fact]
    public void ValidateStorageConfiguration_S3_ProductionLoopbackServiceUrl_ThrowsSanitizedException()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3BucketName"] = "my-bucket",
            ["Storage:S3Region"] = "us-east-1",
            ["Storage:S3AccessKey"] = "access-key",
            ["Storage:S3SecretKey"] = "secret-key",
            ["Storage:S3ServiceUrl"] = "https://localhost:9000/s3"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*S3 service endpoint cannot target localhost or loopback in production*");
    }

    [Fact]
    public void ValidateStorageConfiguration_S3_InvalidServiceUrl_ThrowsSanitizedException()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3BucketName"] = "my-bucket",
            ["Storage:S3Region"] = "us-east-1",
            ["Storage:S3AccessKey"] = "access-key",
            ["Storage:S3SecretKey"] = "secret-key",
            ["Storage:S3ServiceUrl"] = "not-a-valid-url"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Storage:S3ServiceUrl' must be a valid absolute HTTP or HTTPS URL*");
    }

    [Fact]
    public void ValidateStorageConfiguration_S3_MissingRegion_ThrowsSanitizedException()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3BucketName"] = "my-bucket",
            ["Storage:S3ServiceUrl"] = "https://glstabcxyz.supabase.co/storage/v1/s3",
            ["Storage:S3AccessKey"] = "access-key",
            ["Storage:S3SecretKey"] = "secret-key"
            // Missing Storage:S3Region and AWS_REGION
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*an explicit S3 signing region*must be configured*");
    }

    [Fact]
    public void ValidateStorageConfiguration_UnsupportedProvider_ThrowsSanitizedException()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "GoogleCloudStorage"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unsupported storage provider 'GoogleCloudStorage'*");
    }

    [Fact]
    public void ValidateStorageConfiguration_LocalProvider_SucceedsInDevelopment()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Local"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var act = () => Program.ValidateStorageConfiguration(config, isProduction: false);

        act.Should().NotThrow();
    }

    // ──────────────────────────────────────────────────────────
    // 2. S3DocumentStorageService Upload, Download, Delete Tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveDocumentAsync_SupabaseCompatible_DoesNotSendAwsServerSideEncryptionByDefault()
    {
        // Arrange — Supabase Storage does not support AWS SSE headers
        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-private-vault"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        PutObjectRequest? capturedRequest = null;
        _s3Mock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        var content = "Secret Financial Data"u8.ToArray();

        // Act
        var key = await storage.SaveDocumentAsync(content, "financials.pdf", "application/pdf");

        // Assert
        key.Should().MatchRegex("^vault-docs/[a-f0-9]{32}\\.vault$");
        capturedRequest.Should().NotBeNull();
        capturedRequest!.BucketName.Should().Be("supabase-private-vault");
        capturedRequest.Key.Should().Be(key);
        capturedRequest.ContentType.Should().Be("application/pdf");
        capturedRequest.ServerSideEncryptionMethod.Should().BeNull(
            "Supabase Storage S3 API rejects AWS AES256 headers; SSE must be disabled by default");
    }

    [Fact]
    public async Task SaveDocumentAsync_WhenExplicitlyConfigured_EnablesServerSideEncryption()
    {
        // Arrange — AWS S3 deployments that explicitly request SSE
        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "aws-encrypted-vault",
            ["Storage:EnableS3ServerSideEncryption"] = "true"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        PutObjectRequest? capturedRequest = null;
        _s3Mock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        // Act
        await storage.SaveDocumentAsync("Data"u8.ToArray(), "doc.txt", "text/plain");

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ServerSideEncryptionMethod.Should().Be(ServerSideEncryptionMethod.AES256);
    }

    [Fact]
    public async Task SaveDocumentAsync_WhenRemoteWriteFails_ThrowsAndDoesNotFallBackToEphemeralLocalDisk()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault",
            ["Storage:LocalVaultPath"] = _tempLocalDir
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        _s3Mock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Connection refused / Storage unavailable")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            });

        var content = "Critical Payload"u8.ToArray();

        // Act
        var act = () => storage.SaveDocumentAsync(content, "critical.pdf", "application/pdf");

        // Assert — must propagate exception and NOT silently fall back to ephemeral local filesystem
        await act.Should().ThrowAsync<AmazonS3Exception>();

        // Verify no files were created in local directory
        Directory.GetFiles(_tempLocalDir).Should().BeEmpty(
            "failed remote uploads must never write secretly to ephemeral local disk");
    }

    [Fact]
    public async Task GetDocumentStreamAsync_WhenObjectExistsInS3_ReturnsRemoteStream()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        var expectedBytes = "Remote Document Content"u8.ToArray();
        var responseStream = new MemoryStream(expectedBytes);

        var getResponse = new GetObjectResponse
        {
            ResponseStream = responseStream,
            HttpStatusCode = HttpStatusCode.OK
        };

        _s3Mock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(getResponse);

        // Act
        using var stream = await storage.GetDocumentStreamAsync("remote-doc-123.vault");

        // Assert
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().Should().Equal(expectedBytes);
    }

    [Fact]
    public async Task GetDocumentStreamAsync_WhenMissingInS3_FallsBackToLegacyLocalDocumentIfPresent()
    {
        // Arrange — document was created on Render before Supabase Storage migration
        var legacyKey = "legacy-file-456.vault";
        var localFilePath = Path.Combine(_tempLocalDir, legacyKey);
        var legacyBytes = "Legacy Local Content Prior To Supabase"u8.ToArray();
        await File.WriteAllBytesAsync(localFilePath, legacyBytes);

        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault",
            ["Storage:LocalVaultPath"] = _tempLocalDir
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        // S3 returns 404 NotFound
        _s3Mock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });

        // Act
        using var stream = await storage.GetDocumentStreamAsync(legacyKey);

        // Assert — reads legacy local file smoothly
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().Should().Equal(legacyBytes);
    }

    [Fact]
    public async Task GetDocumentStreamAsync_WhenMissingInBothS3AndLocal_ThrowsFileNotFoundException()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault",
            ["Storage:LocalVaultPath"] = _tempLocalDir
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        _s3Mock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });

        // Act
        var act = () => storage.GetDocumentStreamAsync("completely-missing.vault");

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>()
            .WithMessage("*completely-missing.vault*");
    }

    [Fact]
    public async Task DeleteDocumentAsync_CallsS3Delete_AndCleansUpLegacyLocalFile()
    {
        // Arrange
        var legacyKey = "to-be-deleted.vault";
        var localFilePath = Path.Combine(_tempLocalDir, legacyKey);
        await File.WriteAllBytesAsync(localFilePath, "to delete"u8.ToArray());

        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault",
            ["Storage:LocalVaultPath"] = _tempLocalDir
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        DeleteObjectRequest? capturedDelete = null;
        _s3Mock.Setup(s => s.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteObjectRequest, CancellationToken>((req, _) => capturedDelete = req)
            .ReturnsAsync(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.NoContent });

        // Act
        await storage.DeleteDocumentAsync(legacyKey);

        // Assert
        capturedDelete.Should().NotBeNull();
        capturedDelete!.BucketName.Should().Be("supabase-vault");
        capturedDelete.Key.Should().Be(legacyKey);
        File.Exists(localFilePath).Should().BeFalse("legacy local file must be cleaned up on delete");
    }

    // ──────────────────────────────────────────────────────────
    // 3. Controller, Audit Logging & End-to-End Authorization
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_And_Download_EnforcesAuditAndAuthorization()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        _s3Mock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });

        var authOptions = Options.Create(new AuthorizationOptions());
        var authLogger = new Mock<ILogger<DocumentAuthorizationHandler>>();
        var authHandler = new DocumentAuthorizationHandler(_dbContext, authLogger.Object);
        var handlers = new IAuthorizationHandler[] { authHandler };
        var handlerProviderMock = new Mock<IAuthorizationHandlerProvider>();
        handlerProviderMock.Setup(h => h.GetHandlersAsync(It.IsAny<AuthorizationHandlerContext>()))
            .ReturnsAsync(handlers);

        var handlerContextFactoryMock = new Mock<IAuthorizationHandlerContextFactory>();
        handlerContextFactoryMock.Setup(f => f.CreateContext(It.IsAny<IEnumerable<IAuthorizationRequirement>>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>()))
            .Returns<IEnumerable<IAuthorizationRequirement>, ClaimsPrincipal, object?>((reqs, u, res) =>
                new AuthorizationHandlerContext(reqs, u, res));

        var authService = new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(authOptions),
            handlerProviderMock.Object,
            new Mock<ILogger<DefaultAuthorizationService>>().Object,
            handlerContextFactoryMock.Object,
            new DefaultAuthorizationEvaluator(),
            authOptions);

        var controller = new DocumentsController(
            _dbContext,
            authService,
            storage,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerId = Guid.NewGuid();
        var user = new ApplicationUser { Id = ownerId, Email = "owner@vault.local", FullName = "Owner User" };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Email, "owner@vault.local"),
            new(ClaimTypes.Role, Roles.Owner)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = principal,
                TraceIdentifier = "corr-test-123"
            }
        };

        var fileContent = "Confidential report content"u8.ToArray();
        using var formStream = new MemoryStream(fileContent);
        var formFile = new FormFile(formStream, 0, fileContent.Length, "file", "q3_report.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

        // Act 1: Upload document
        var uploadResult = await controller.UploadDocument(formFile, "Q3 Financial Report");

        // Assert 1: Upload succeeded
        var createdAt = uploadResult.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = createdAt.Value.Should().BeOfType<DocumentDto>().Subject;
        dto.Title.Should().Be("Q3 Financial Report");
        dto.FileName.Should().Be("q3_report.pdf");

        // Verify S3 Put was invoked
        _s3Mock.Verify(s => s.PutObjectAsync(It.Is<PutObjectRequest>(r => r.BucketName == "supabase-vault"), It.IsAny<CancellationToken>()), Times.Once);

        // Verify audit log for upload
        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "UploadDocument",
            "Document",
            dto.Id.ToString(),
            It.Is<string>(s => s.Contains("q3_report.pdf")),
            "Success",
            It.IsAny<string?>(),
            "corr-test-123"), Times.Once);

        // Setup S3 Get response for download
        _s3Mock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(fileContent),
                HttpStatusCode = HttpStatusCode.OK
            });

        // Act 2: Owner downloads document
        var downloadResult = await controller.DownloadDocument(dto.Id);
        downloadResult.Should().BeOfType<FileStreamResult>();

        // Verify audit log for download
        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "DownloadDocument",
            "Document",
            dto.Id.ToString(),
            It.Is<string>(s => s.Contains("q3_report.pdf")),
            "Success",
            It.IsAny<string?>(),
            "corr-test-123"), Times.Once);

        // Act 3: Unauthorized user attempts download
        var strangerId = Guid.NewGuid();
        var strangerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, strangerId.ToString()),
            new(ClaimTypes.Email, "stranger@vault.local"),
            new(ClaimTypes.Role, Roles.Viewer)
        };
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(strangerClaims, "Bearer"));

        var unauthorizedDownload = await controller.DownloadDocument(dto.Id);
        unauthorizedDownload.Should().BeOfType<ForbidResult>("unauthorized user must be denied access to documents");
    }

    [Fact]
    public async Task GetDocumentStreamAsync_WhenRemoteServiceUnavailable_ThrowsAndDoesNotFallBackToLocal()
    {
        // Arrange — remote S3 outage (503 Service Unavailable). Even if local file exists, must throw rather than mask the outage.
        var legacyKey = "test-doc.vault";
        var localFilePath = Path.Combine(_tempLocalDir, legacyKey);
        await File.WriteAllBytesAsync(localFilePath, "Existing Local Bytes"u8.ToArray());

        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault",
            ["Storage:LocalVaultPath"] = _tempLocalDir
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        _s3Mock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Storage Service Unavailable") { StatusCode = HttpStatusCode.ServiceUnavailable });

        // Act
        var act = () => storage.GetDocumentStreamAsync(legacyKey);

        // Assert — must bubble up AmazonS3Exception and never serve local disk during remote outage
        await act.Should().ThrowAsync<AmazonS3Exception>();
    }

    [Fact]
    public async Task GetDocumentStreamAsync_WhenRemoteAuthenticationFails_ThrowsAndDoesNotFallBackToLocal()
    {
        // Arrange — S3 credentials invalid (403 Forbidden). Must propagate error.
        var legacyKey = "auth-doc.vault";
        var localFilePath = Path.Combine(_tempLocalDir, legacyKey);
        await File.WriteAllBytesAsync(localFilePath, "Local Bytes"u8.ToArray());

        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault",
            ["Storage:LocalVaultPath"] = _tempLocalDir
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        _s3Mock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Forbidden") { StatusCode = HttpStatusCode.Forbidden });

        // Act
        var act = () => storage.GetDocumentStreamAsync(legacyKey);

        // Assert
        await act.Should().ThrowAsync<AmazonS3Exception>();
    }

    [Fact]
    public async Task GetDocumentStreamAsync_RemoteNamespacedKey_WhenMissingInS3_ThrowsFileNotFoundAndNeverChecksLocal()
    {
        // Arrange — Key begins with 'vault-docs/' (definitively remote S3 object).
        var remoteKey = "vault-docs/11223344556677889900aabbccddeeff.vault";
        var localFilePath = Path.Combine(_tempLocalDir, "11223344556677889900aabbccddeeff.vault");
        await File.WriteAllBytesAsync(localFilePath, "Accidental local match"u8.ToArray());

        var configData = new Dictionary<string, string?>
        {
            ["Storage:S3BucketName"] = "supabase-vault",
            ["Storage:LocalVaultPath"] = _tempLocalDir
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var storage = new S3DocumentStorageService(_s3Mock.Object, config, _envMock.Object, _storageLoggerMock.Object);

        _s3Mock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("NoSuchKey") { StatusCode = HttpStatusCode.NotFound });

        // Act
        var act = () => storage.GetDocumentStreamAsync(remoteKey);

        // Assert — S3 namespaced object must throw FileNotFoundException and NOT fall back to local disk
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeleteDocument_WhenCallerUnauthorized_ReturnsForbid_AuditsDenied_AndNeverCallsStorage()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Protected Strategy Doc",
            FileName = "strategy.pdf",
            ContentType = "application/pdf",
            StorageKey = "vault-docs/strat-key.vault",
            OwnerId = ownerId,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authOptions = Options.Create(new AuthorizationOptions());
        var authLogger = new Mock<ILogger<DocumentAuthorizationHandler>>();
        var authHandler = new DocumentAuthorizationHandler(_dbContext, authLogger.Object);
        var handlers = new IAuthorizationHandler[] { authHandler };
        var handlerProviderMock = new Mock<IAuthorizationHandlerProvider>();
        handlerProviderMock.Setup(h => h.GetHandlersAsync(It.IsAny<AuthorizationHandlerContext>()))
            .ReturnsAsync(handlers);
        var handlerContextFactoryMock = new Mock<IAuthorizationHandlerContextFactory>();
        handlerContextFactoryMock.Setup(f => f.CreateContext(It.IsAny<IEnumerable<IAuthorizationRequirement>>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>()))
            .Returns<IEnumerable<IAuthorizationRequirement>, ClaimsPrincipal, object?>((reqs, u, res) =>
                new AuthorizationHandlerContext(reqs, u, res));
        var authService = new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(authOptions),
            handlerProviderMock.Object,
            new Mock<ILogger<DefaultAuthorizationService>>().Object,
            handlerContextFactoryMock.Object,
            new DefaultAuthorizationEvaluator(),
            authOptions);

        var storageMock = new Mock<IDocumentStorageService>();
        var controller = new DocumentsController(
            _dbContext,
            authService,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var viewerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, viewerId.ToString()),
            new(ClaimTypes.Email, "viewer@vault.local"),
            new(ClaimTypes.Role, Roles.Viewer)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(viewerClaims, "Bearer")),
                TraceIdentifier = "audit-corr-deny-1"
            }
        };

        // Act — Viewer attempts to delete document
        var result = await controller.DeleteDocument(doc.Id);

        // Assert — 403 Forbidden, storage never touched, audit event accurately records denial
        result.Should().BeOfType<ForbidResult>();
        storageMock.Verify(s => s.DeleteDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditMock.Verify(a => a.LogEventAsync(
            viewerId,
            "DeleteDocumentDenied",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Delete permission denied")),
            "Forbidden",
            It.IsAny<string?>(),
            "audit-corr-deny-1"), Times.Once);

        var dbDoc = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        dbDoc.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteDocument_WhenStorageDeleteFails_Returns500_AuditsFailure_AndPreservesDatabaseState()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Mission Critical Data",
            FileName = "critical.pdf",
            ContentType = "application/pdf",
            StorageKey = "vault-docs/fail-delete.vault",
            OwnerId = ownerId,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authOptions = Options.Create(new AuthorizationOptions());
        var authLogger = new Mock<ILogger<DocumentAuthorizationHandler>>();
        var authHandler = new DocumentAuthorizationHandler(_dbContext, authLogger.Object);
        var handlers = new IAuthorizationHandler[] { authHandler };
        var handlerProviderMock = new Mock<IAuthorizationHandlerProvider>();
        handlerProviderMock.Setup(h => h.GetHandlersAsync(It.IsAny<AuthorizationHandlerContext>()))
            .ReturnsAsync(handlers);
        var handlerContextFactoryMock = new Mock<IAuthorizationHandlerContextFactory>();
        handlerContextFactoryMock.Setup(f => f.CreateContext(It.IsAny<IEnumerable<IAuthorizationRequirement>>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>()))
            .Returns<IEnumerable<IAuthorizationRequirement>, ClaimsPrincipal, object?>((reqs, u, res) =>
                new AuthorizationHandlerContext(reqs, u, res));
        var authService = new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(authOptions),
            handlerProviderMock.Object,
            new Mock<ILogger<DefaultAuthorizationService>>().Object,
            handlerContextFactoryMock.Object,
            new DefaultAuthorizationEvaluator(),
            authOptions);

        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.DeleteDocumentAsync(doc.StorageKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Remote S3 delete operation timed out")
            {
                StatusCode = HttpStatusCode.InternalServerError
            });

        var controller = new DocumentsController(
            _dbContext,
            authService,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Email, "owner@vault.local"),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-corr-fail-1"
            }
        };

        // Act — Owner attempts deletion, but remote storage fails
        var result = await controller.DeleteDocument(doc.Id);

        // Assert — 500 error returned, audit records failure, document is NOT marked deleted
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "DeleteDocumentFailed",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Storage deletion failed")),
            "Failure",
            It.IsAny<string?>(),
            "audit-corr-fail-1"), Times.Once);

        var dbDoc = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        dbDoc.IsDeleted.Should().BeFalse("document must NOT be marked deleted when storage deletion fails");
    }

    [Fact]
    public async Task DeleteDocument_WhenRemoteDeleteSucceeds_ButDatabaseSaveFails_ReportsStateMismatch_AndRecoversOnRetry()
    {
        // Arrange — Custom DbContext that fails on first save and succeeds on retry
        var dbName = "FlakyDb_" + Guid.NewGuid().ToString("N");
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var flakyDb = new FlakyTestDbContext(dbOptions);
        var ownerId = Guid.NewGuid();
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Financial Records",
            FileName = "records.pdf",
            ContentType = "application/pdf",
            StorageKey = "vault-docs/mismatch-test.vault",
            OwnerId = ownerId,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow
        };
        flakyDb.Documents.Add(doc);
        flakyDb.DisableFailure = true;
        await flakyDb.SaveChangesAsync();
        flakyDb.DisableFailure = false; // Next SaveChangesAsync will fail!

        var authOptions = Options.Create(new AuthorizationOptions());
        var authLogger = new Mock<ILogger<DocumentAuthorizationHandler>>();
        var authHandler = new DocumentAuthorizationHandler(flakyDb, authLogger.Object);
        var handlers = new IAuthorizationHandler[] { authHandler };
        var handlerProviderMock = new Mock<IAuthorizationHandlerProvider>();
        handlerProviderMock.Setup(h => h.GetHandlersAsync(It.IsAny<AuthorizationHandlerContext>()))
            .ReturnsAsync(handlers);
        var handlerContextFactoryMock = new Mock<IAuthorizationHandlerContextFactory>();
        handlerContextFactoryMock.Setup(f => f.CreateContext(It.IsAny<IEnumerable<IAuthorizationRequirement>>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>()))
            .Returns<IEnumerable<IAuthorizationRequirement>, ClaimsPrincipal, object?>((reqs, u, res) =>
                new AuthorizationHandlerContext(reqs, u, res));
        var authService = new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(authOptions),
            handlerProviderMock.Object,
            new Mock<ILogger<DefaultAuthorizationService>>().Object,
            handlerContextFactoryMock.Object,
            new DefaultAuthorizationEvaluator(),
            authOptions);

        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.DeleteDocumentAsync(doc.StorageKey, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask); // S3 delete succeeds!

        var controller = new DocumentsController(
            flakyDb,
            authService,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Email, "owner@vault.local"),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-mismatch-1"
            }
        };

        // Act 1: First attempt — S3 succeeds, but DB save fails
        var firstResult = await controller.DeleteDocument(doc.Id);

        // Assert 1: Returns 500, audits DeleteDocumentStateMismatch with Failure, does not claim success
        var statusResult = firstResult.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        statusResult.Value?.ToString().Should().Contain("retry the delete request to synchronize state");

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "DeleteDocumentStateMismatch",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Storage binary was deleted but database update failed")),
            "Failure",
            It.IsAny<string?>(),
            "audit-mismatch-1"), Times.Once);

        // Act 2: User retries the delete operation
        flakyDb.DisableFailure = true; // Transient DB conflict resolved
        controller.ControllerContext.HttpContext.TraceIdentifier = "audit-mismatch-retry";
        var retryResult = await controller.DeleteDocument(doc.Id);

        // Assert 2: Retry succeeds, returns 204 NoContent, audits DeleteDocument with Success, DB is now marked deleted
        retryResult.Should().BeOfType<NoContentResult>();

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "DeleteDocument",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("soft-deleted")),
            "Success",
            It.IsAny<string?>(),
            "audit-mismatch-retry"), Times.Once);

        var finalDbDoc = await flakyDb.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        finalDbDoc.IsDeleted.Should().BeTrue("document state must be successfully synchronized on retry");
    }

    [Fact]
    public async Task DownloadDocument_WhenFileMissingFromStorage_ReturnsNotFound_AndAuditsFailure()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Vanished Document",
            FileName = "vanished.pdf",
            ContentType = "application/pdf",
            StorageKey = "vault-docs/vanished.vault",
            OwnerId = ownerId,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authOptions = Options.Create(new AuthorizationOptions());
        var authLogger = new Mock<ILogger<DocumentAuthorizationHandler>>();
        var authHandler = new DocumentAuthorizationHandler(_dbContext, authLogger.Object);
        var handlers = new IAuthorizationHandler[] { authHandler };
        var handlerProviderMock = new Mock<IAuthorizationHandlerProvider>();
        handlerProviderMock.Setup(h => h.GetHandlersAsync(It.IsAny<AuthorizationHandlerContext>()))
            .ReturnsAsync(handlers);
        var handlerContextFactoryMock = new Mock<IAuthorizationHandlerContextFactory>();
        handlerContextFactoryMock.Setup(f => f.CreateContext(It.IsAny<IEnumerable<IAuthorizationRequirement>>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>()))
            .Returns<IEnumerable<IAuthorizationRequirement>, ClaimsPrincipal, object?>((reqs, u, res) =>
                new AuthorizationHandlerContext(reqs, u, res));
        var authService = new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(authOptions),
            handlerProviderMock.Object,
            new Mock<ILogger<DefaultAuthorizationService>>().Object,
            handlerContextFactoryMock.Object,
            new DefaultAuthorizationEvaluator(),
            authOptions);

        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.GetDocumentStreamAsync(doc.StorageKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("Object vanished from storage bucket"));

        var controller = new DocumentsController(
            _dbContext,
            authService,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Email, "owner@vault.local"),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-missing-download"
            }
        };

        // Act
        var result = await controller.DownloadDocument(doc.Id);

        // Assert — Returns 404 NotFound and logs DownloadDocumentFailed with NotFound status
        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value?.ToString().Should().Contain("could not be found in storage");

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "DownloadDocumentFailed",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("missing from storage")),
            "NotFound",
            It.IsAny<string?>(),
            "audit-missing-download"), Times.Once);
    }

    // ──────────────────────────────────────────────────────────
    // 4. Legacy Document Migration Tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MigrateLegacyDocument_Success_UploadsToS3_VerifiesReadBack_AndUpdatesStorageKey()
    {
        // Arrange — Legacy document with flat local key (e.g. "legacy-contract.vault")
        var ownerId = Guid.NewGuid();
        var legacyKey = "a1b2c3d4e5f678901234567890abcdef.vault";
        var fileContent = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x20, 0x31, 0x2e, 0x34 }; // %PDF 1.4
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileContent)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Legacy Production Contract",
            FileName = "contract.pdf",
            ContentType = "application/pdf",
            StorageKey = legacyKey,
            FileSizeBytes = fileContent.Length,
            OwnerId = ownerId,
            CreatedAtUtc = DateTime.UtcNow.AddMonths(-2),
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authOptions = Options.Create(new AuthorizationOptions());
        var authLogger = new Mock<ILogger<DocumentAuthorizationHandler>>();
        var authHandler = new DocumentAuthorizationHandler(_dbContext, authLogger.Object);
        var handlers = new IAuthorizationHandler[] { authHandler };
        var handlerProviderMock = new Mock<IAuthorizationHandlerProvider>();
        handlerProviderMock.Setup(h => h.GetHandlersAsync(It.IsAny<AuthorizationHandlerContext>()))
            .ReturnsAsync(handlers);
        var handlerContextFactoryMock = new Mock<IAuthorizationHandlerContextFactory>();
        handlerContextFactoryMock.Setup(f => f.CreateContext(It.IsAny<IEnumerable<IAuthorizationRequirement>>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>()))
            .Returns<IEnumerable<IAuthorizationRequirement>, ClaimsPrincipal, object?>((reqs, u, res) =>
                new AuthorizationHandlerContext(reqs, u, res));
        var authService = new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(authOptions),
            handlerProviderMock.Object,
            new Mock<ILogger<DefaultAuthorizationService>>().Object,
            handlerContextFactoryMock.Object,
            new DefaultAuthorizationEvaluator(),
            authOptions);

        var newS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "contract.pdf", "application/pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(newS3Key);

        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(fileContent));

        var controller = new DocumentsController(
            _dbContext,
            authService,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Email, "owner@vault.local"),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-migrate-success"
            }
        };

        using var stream = new MemoryStream(fileContent);
        var formFile = new FormFile(stream, 0, fileContent.Length, "file", "contract.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, expectedSha256);

        // Assert — Returns 200 OK, StorageKey updated to new S3 key, audit logged
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var updatedDoc = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        updatedDoc.StorageKey.Should().Be(newS3Key);
        updatedDoc.StorageKey.Should().StartWith("vault-docs/");
        updatedDoc.Title.Should().Be("Legacy Production Contract");
        updatedDoc.FileName.Should().Be("contract.pdf");
        updatedDoc.OwnerId.Should().Be(ownerId);
        updatedDoc.UpdatedAtUtc.Should().NotBeNull();

        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "contract.pdf", "application/pdf", It.IsAny<CancellationToken>()), Times.Once);
        storageMock.Verify(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()), Times.Once,
            "read-back verification must occur before database update");

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentStorage",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Successfully migrated document") && s.Contains(newS3Key)),
            "Success",
            It.IsAny<string?>(),
            "audit-migrate-success"), Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyDocument_Success_WhenAdminAuthorized_UploadsAndUpdatesStorageKey()
    {
        // Arrange — Admin migrating document owned by another user
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var legacyKey = "legacy-admin-test.vault";
        var fileContent = new byte[] { 0x50, 0x4B, 0x03, 0x04 }; // ZIP/DOCX header
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileContent)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Admin Migrated Report",
            FileName = "report.docx",
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            StorageKey = legacyKey,
            FileSizeBytes = fileContent.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var newS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "report.docx", doc.ContentType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newS3Key);
        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(fileContent));

        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var adminClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, adminId.ToString()),
            new(ClaimTypes.Email, "admin@vault.local"),
            new(ClaimTypes.Role, Roles.Admin)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(adminClaims, "Bearer")),
                TraceIdentifier = "audit-admin-migrate"
            }
        };

        using var stream = new MemoryStream(fileContent);
        var formFile = new FormFile(stream, 0, fileContent.Length, "file", "report.docx");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, expectedSha256);

        // Assert — Admin authorization succeeds
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var updatedDoc = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        updatedDoc.StorageKey.Should().Be(newS3Key);

        _auditMock.Verify(a => a.LogEventAsync(
            adminId,
            "MigrateDocumentStorage",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Successfully migrated document") && s.Contains(newS3Key)),
            "Success",
            It.IsAny<string?>(),
            "audit-admin-migrate"), Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenCallerIsNonOwnerCollaboratorWithEdit_ReturnsForbid_AuditsDenied()
    {
        // Arrange — Collaborator who only has PermissionLevel.Edit is forbidden from migrating storage
        var ownerId = Guid.NewGuid();
        var collaboratorId = Guid.NewGuid();
        var legacyKey = "collaborator-test.vault";
        var fileBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Shared Project",
            FileName = "project.pdf",
            ContentType = "application/pdf",
            StorageKey = legacyKey,
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        doc.Permissions.Add(new DocumentPermission
        {
            DocumentId = doc.Id,
            UserId = collaboratorId,
            PermissionLevel = PermissionLevel.Edit
        });
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var collaboratorClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, collaboratorId.ToString()),
            new(ClaimTypes.Email, "collab@vault.local"),
            new(ClaimTypes.Role, Roles.Viewer)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(collaboratorClaims, "Bearer")),
                TraceIdentifier = "audit-collab-denied"
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "project.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, expectedSha256);

        // Assert — 403 Forbid
        result.Should().BeOfType<ForbidResult>();
        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditMock.Verify(a => a.LogEventAsync(
            collaboratorId,
            "MigrateDocumentDenied",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Storage migration denied")),
            "Forbidden",
            It.IsAny<string?>(),
            "audit-collab-denied"), Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenCallerUnauthorizedViewer_ReturnsForbid_AuditsDenied()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var fileBytes = new byte[] { 1, 2, 3 };
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Confidential Strategy",
            FileName = "strat.pdf",
            ContentType = "application/pdf",
            StorageKey = "local-key-123.vault",
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authOptions = Options.Create(new AuthorizationOptions());
        var authLogger = new Mock<ILogger<DocumentAuthorizationHandler>>();
        var authHandler = new DocumentAuthorizationHandler(_dbContext, authLogger.Object);
        var handlers = new IAuthorizationHandler[] { authHandler };
        var handlerProviderMock = new Mock<IAuthorizationHandlerProvider>();
        handlerProviderMock.Setup(h => h.GetHandlersAsync(It.IsAny<AuthorizationHandlerContext>()))
            .ReturnsAsync(handlers);
        var handlerContextFactoryMock = new Mock<IAuthorizationHandlerContextFactory>();
        handlerContextFactoryMock.Setup(f => f.CreateContext(It.IsAny<IEnumerable<IAuthorizationRequirement>>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>()))
            .Returns<IEnumerable<IAuthorizationRequirement>, ClaimsPrincipal, object?>((reqs, u, res) =>
                new AuthorizationHandlerContext(reqs, u, res));
        var authService = new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(authOptions),
            handlerProviderMock.Object,
            new Mock<ILogger<DefaultAuthorizationService>>().Object,
            handlerContextFactoryMock.Object,
            new DefaultAuthorizationEvaluator(),
            authOptions);

        var storageMock = new Mock<IDocumentStorageService>();
        var controller = new DocumentsController(
            _dbContext,
            authService,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var viewerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, viewerId.ToString()),
            new(ClaimTypes.Email, "viewer@vault.local"),
            new(ClaimTypes.Role, Roles.Viewer)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(viewerClaims, "Bearer")),
                TraceIdentifier = "audit-migrate-deny"
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "strat.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, expectedSha256);

        // Assert — 403 Forbid, storage never called, denial audited
        result.Should().BeOfType<ForbidResult>();
        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditMock.Verify(a => a.LogEventAsync(
            viewerId,
            "MigrateDocumentDenied",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Storage migration denied")),
            "Forbidden",
            It.IsAny<string?>(),
            "audit-migrate-deny"), Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenExpectedSha256MissingOrWhitespace_ReturnsBadRequest()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var fileBytes = new byte[] { 1, 2, 3, 4 };
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Missing Checksum Doc",
            FileName = "missing_cs.pdf",
            ContentType = "application/pdf",
            StorageKey = "missing-cs.vault",
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer"))
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "missing_cs.pdf");

        // Act — pass empty string
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, "   ");

        // Assert — 400 BadRequest requiring expectedSha256
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value?.ToString().Should().Contain("expectedSha256 is required");
        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenExpectedSha256Not64HexCharacters_ReturnsBadRequest()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var fileBytes = new byte[] { 1, 2, 3, 4 };
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Bad Hex Doc",
            FileName = "bad_hex.pdf",
            ContentType = "application/pdf",
            StorageKey = "bad-hex.vault",
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer"))
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "bad_hex.pdf");

        // Act — pass non-hex and invalid length strings
        var resultTooShort = await controller.MigrateLegacyDocument(doc.Id, formFile, "abc1234");
        var resultInvalidChars = await controller.MigrateLegacyDocument(doc.Id, formFile, new string('z', 64));

        // Assert — 400 BadRequest requiring 64 hexadecimal characters
        var badRequest1 = resultTooShort.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest1.Value?.ToString().Should().Contain("64 hexadecimal characters");

        var badRequest2 = resultInvalidChars.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest2.Value?.ToString().Should().Contain("64 hexadecimal characters");
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenExpectedSha256ChecksumMismatches_ReturnsBadRequest()
    {
        // Arrange — Uploaded binary does not match the caller's expectedSha256
        var ownerId = Guid.NewGuid();
        var fileBytes = new byte[] { 1, 2, 3, 4, 5 };
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Checksum Test",
            FileName = "check.pdf",
            ContentType = "application/pdf",
            StorageKey = "checksum-test.vault",
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer"))
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "check.pdf");

        // Act — Pass a mismatched valid 64-char expected hash
        var wrongExpectedHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, wrongExpectedHash);

        // Assert — 400 BadRequest with checksum mismatch
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value?.ToString().Should().Contain("Binary SHA-256 checksum mismatch");
        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenFileSizeMismatchesDatabase_ReturnsBadRequest()
    {
        // Arrange — Document has registered size 100 bytes, but uploaded file is 4 bytes
        var ownerId = Guid.NewGuid();
        var uploadedBytes = new byte[] { 1, 2, 3, 4 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(uploadedBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Size Mismatch Test",
            FileName = "doc.pdf",
            ContentType = "application/pdf",
            StorageKey = "local-size-test.vault",
            FileSizeBytes = 100, // Expected 100 bytes
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer"))
            }
        };

        using var stream = new MemoryStream(uploadedBytes);
        var formFile = new FormFile(stream, 0, uploadedBytes.Length, "file", "doc.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, validHash);

        // Assert — 400 BadRequest with size mismatch message
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value?.ToString().Should().Contain("File size mismatch");
        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenFilenameMismatches_ReturnsBadRequest()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var fileBytes = new byte[] { 1, 2, 3 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Registered Document",
            FileName = "actual_name.pdf",
            ContentType = "application/pdf",
            StorageKey = "flat-key.vault",
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-mismatch-filename"
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var wrongFile = new FormFile(stream, 0, fileBytes.Length, "file", "wrong_name.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, wrongFile, validHash);

        // Assert — 400 BadRequest with explanatory message
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value?.ToString().Should().Contain("does not match registered document file name");
        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenReadBackVerificationFails_CleansUpRemote_LeavesDatabaseUntouched()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var legacyKey = "legacy-to-verify.vault";
        var fileBytes = new byte[] { 1, 2, 3, 4 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Corrupt Verify Test",
            FileName = "verify.pdf",
            ContentType = "application/pdf",
            StorageKey = legacyKey,
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var newS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "verify.pdf", "application/pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(newS3Key);

        // Verification read-back throws FileNotFoundException or read failure
        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("Object vanished immediately after write"));

        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-verify-fail"
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "verify.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, validHash);

        // Assert — Returns 500, cleans up new S3 object, leaves DB unchanged with legacyKey
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        storageMock.Verify(s => s.DeleteDocumentAsync(newS3Key, It.IsAny<CancellationToken>()), Times.Once,
            "failed verification must clean up unverified remote object");

        var dbDoc = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        dbDoc.StorageKey.Should().Be(legacyKey, "database StorageKey must remain untouched on verification failure");

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentVerificationFailed",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Remote read-back verification failed")),
            "Failure",
            It.IsAny<string?>(),
            "audit-verify-fail"), Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenVerificationFails_AndCleanupThrows_LogsWarning_LeavesDatabaseUntouched()
    {
        // Arrange — Verification fails, and S3 cleanup throws (e.g. S3 network outage)
        var ownerId = Guid.NewGuid();
        var legacyKey = "cleanup-fail.vault";
        var fileBytes = new byte[] { 10, 20, 30 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Cleanup Failure Test",
            FileName = "cleanup.pdf",
            ContentType = "application/pdf",
            StorageKey = legacyKey,
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var newS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "cleanup.pdf", "application/pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(newS3Key);

        // Verification returns wrong bytes (hash mismatch)
        var corruptedBytes = new byte[] { 99, 99, 99 };
        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(corruptedBytes));

        // DeleteDocumentAsync throws!
        storageMock.Setup(s => s.DeleteDocumentAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.S3.AmazonS3Exception("Connection dropped during cleanup"));

        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-cleanup-warning"
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "cleanup.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, validHash);

        // Assert — Does not crash, returns 500, DB untouched, audits OrphanedStorageObjectWarning
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        var dbDoc = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        dbDoc.StorageKey.Should().Be(legacyKey, "DB must remain untouched");

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "OrphanedStorageObjectWarning",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("automated cleanup failed") && s.Contains(newS3Key)),
            "Warning",
            It.IsAny<string?>(),
            "audit-cleanup-warning"), Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenDatabaseSaveFails_CleansUpRemote_LeavesOldKeyInDatabase()
    {
        // Arrange — Storage upload and verification succeed, but EF Core SaveChangesAsync throws
        var flakyOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: $"FlakyMigrateDb_{Guid.NewGuid()}")
            .Options;

        using var flakyDb = new FlakyTestDbContext(flakyOptions);
        var ownerId = Guid.NewGuid();
        var legacyKey = "flaky-db.vault";
        var fileBytes = new byte[] { 1, 2, 3, 4 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Flaky DB Document",
            FileName = "flaky.pdf",
            ContentType = "application/pdf",
            StorageKey = legacyKey,
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        flakyDb.DisableFailure = true;
        flakyDb.Documents.Add(doc);
        await flakyDb.SaveChangesAsync();
        flakyDb.DisableFailure = false; // Next SaveChangesAsync will throw

        var authServiceMock = new Mock<IAuthorizationService>();
        var newS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "flaky.pdf", "application/pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(newS3Key);
        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(fileBytes));

        var controller = new DocumentsController(
            flakyDb,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-db-fail"
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "flaky.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, validHash);

        // Assert — Returns 500, cleans up remote object, records audit
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        storageMock.Verify(s => s.DeleteDocumentAsync(newS3Key, It.IsAny<CancellationToken>()), Times.Once,
            "remote object must be cleaned up when DB save fails");

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentDatabaseFailed",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains("Database StorageKey update failed")),
            "Failure",
            It.IsAny<string?>(),
            "audit-db-fail"), Times.Once);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenAlreadyMigrated_AndRemoteObjectVerified_ReturnsOk_DoesNotDuplicateAudit()
    {
        // Arrange — Document already has vault-docs/ key and object is verified in storage
        var ownerId = Guid.NewGuid();
        var existingS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var fileContent = new byte[] { 1, 2, 3, 4 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileContent)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Already Migrated File",
            FileName = "already.pdf",
            ContentType = "application/pdf",
            StorageKey = existingS3Key,
            FileSizeBytes = fileContent.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);

        // Existing success audit event already committed in DB
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            SequenceNumber = 1,
            UserId = ownerId,
            Action = "MigrateDocumentStorage",
            EntityType = "Document",
            EntityId = doc.Id.ToString(),
            Status = "Success",
            TimestampUtc = DateTime.UtcNow.AddMinutes(-5),
            PreviousHash = AuditService.GenesisHash,
            CurrentHash = "dummy-hash-1"
        });
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        // Remote read-back verification succeeds
        storageMock.Setup(s => s.GetDocumentStreamAsync(existingS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(fileContent));

        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Email, "owner@vault.local"),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-already-migrated"
            }
        };

        using var stream = new MemoryStream(fileContent);
        var formFile = new FormFile(stream, 0, fileContent.Length, "file", "already.pdf");

        // Act — Attempt migration again on already-migrated document
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, validHash);

        // Assert — Returns 200 OK with AlreadyMigrated: true, no storage upload attempted, NO duplicate audit event
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value?.ToString().Should().Contain("AlreadyMigrated = True");

        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentStorage",
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>()), Times.Never, "idempotent retry must not create duplicate success audit events");
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenAlreadyMigrated_AndRemoteObjectMissing_Returns502_LeavesDatabaseUntouched()
    {
        // Arrange — Document has vault-docs/ key in DB, but object is missing in remote storage
        var ownerId = Guid.NewGuid();
        var missingS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var fileContent = new byte[] { 1, 2, 3, 4 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileContent)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Ghost Document",
            FileName = "ghost.pdf",
            ContentType = "application/pdf",
            StorageKey = missingS3Key,
            FileSizeBytes = fileContent.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        // Remote read-back throws FileNotFoundException
        storageMock.Setup(s => s.GetDocumentStreamAsync(missingS3Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("Object missing in bucket"));

        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer"))
            }
        };

        using var stream = new MemoryStream(fileContent);
        var formFile = new FormFile(stream, 0, fileContent.Length, "file", "ghost.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, validHash);

        // Assert — Returns 502 Bad Gateway, does not re-upload or touch database
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status502BadGateway);

        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "must never silently re-upload over an unverified remote object key");

        var dbDoc = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        dbDoc.StorageKey.Should().Be(missingS3Key, "database key must remain untouched");
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenAlreadyMigrated_AndStorageOutage_Returns502_LeavesDatabaseUntouched()
    {
        // Arrange — Document has vault-docs/ key in DB, but S3 storage suffers network outage
        var ownerId = Guid.NewGuid();
        var s3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var fileContent = new byte[] { 1, 2, 3, 4 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileContent)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Outage Document",
            FileName = "outage.pdf",
            ContentType = "application/pdf",
            StorageKey = s3Key,
            FileSizeBytes = fileContent.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var storageMock = new Mock<IDocumentStorageService>();
        // Remote storage outage
        storageMock.Setup(s => s.GetDocumentStreamAsync(s3Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.S3.AmazonS3Exception("Connection refused / timeout"));

        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer"))
            }
        };

        using var stream = new MemoryStream(fileContent);
        var formFile = new FormFile(stream, 0, fileContent.Length, "file", "outage.pdf");

        // Act
        var result = await controller.MigrateLegacyDocument(doc.Id, formFile, validHash);

        // Assert — Returns 502 Bad Gateway
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status502BadGateway);

        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MigrateLegacyDocument_WhenAuditLoggingFailsAfterDbSave_CompletesMigrationSafely_AndRetryRecoversAuditWithoutDuplicate()
    {
        // Arrange — Storage update & DB save succeed, but audit log throws
        var ownerId = Guid.NewGuid();
        var legacyKey = "audit-fail-test.vault";
        var fileBytes = new byte[] { 1, 2, 3, 4 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Audit Fail Doc",
            FileName = "audit_fail.pdf",
            ContentType = "application/pdf",
            StorageKey = legacyKey,
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };
        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync();

        var authServiceMock = new Mock<IAuthorizationService>();
        var newS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var storageMock = new Mock<IDocumentStorageService>();
        storageMock.Setup(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "audit_fail.pdf", "application/pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(newS3Key);
        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(fileBytes));

        // Setup audit service to throw once on initial success log
        _auditMock.Setup(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentStorage",
            "Document",
            doc.Id.ToString(),
            It.IsAny<string?>(),
            "Success",
            It.IsAny<string?>(),
            "audit-fail-trace"))
            .ThrowsAsync(new InvalidOperationException("Audit store transient connection reset"));

        var controller = new DocumentsController(
            _dbContext,
            authServiceMock.Object,
            storageMock.Object,
            _auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "audit-fail-trace"
            }
        };

        using var stream = new MemoryStream(fileBytes);
        var formFile = new FormFile(stream, 0, fileBytes.Length, "file", "audit_fail.pdf");

        // Act 1 — Initial migration run
        var result1 = await controller.MigrateLegacyDocument(doc.Id, formFile, validHash);

        // Assert 1 — DB is updated to new S3 key and returns 200 OK
        var okResult1 = result1.Should().BeOfType<OkObjectResult>().Subject;
        okResult1.StatusCode.Should().Be(StatusCodes.Status200OK);

        var dbDoc = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        dbDoc.StorageKey.Should().Be(newS3Key);

        // Reset audit mock to succeed on subsequent retry
        _auditMock.Reset();

        // Setup storageMock to continue returning valid stream for newS3Key verification during retry
        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(fileBytes));

        // Act 2 — Caller retries migration request
        using var stream2 = new MemoryStream(fileBytes);
        var formFile2 = new FormFile(stream2, 0, fileBytes.Length, "file", "audit_fail.pdf");
        controller.ControllerContext.HttpContext.TraceIdentifier = "audit-retry-trace";

        var result2 = await controller.MigrateLegacyDocument(doc.Id, formFile2, validHash);

        // Assert 2 — Idempotent check succeeds, logs the missing audit event, returns 200 OK
        var okResult2 = result2.Should().BeOfType<OkObjectResult>().Subject;
        okResult2.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult2.Value?.ToString().Should().Contain("AlreadyMigrated = True");

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentStorage",
            "Document",
            doc.Id.ToString(),
            It.Is<string>(s => s.Contains(newS3Key) && s.Contains(validHash)),
            "Success",
            It.IsAny<string?>(),
            "audit-retry-trace"), Times.Once);

        // Now commit the audit event to DB so it is present for subsequent retries
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            SequenceNumber = 2,
            UserId = ownerId,
            Action = "MigrateDocumentStorage",
            EntityType = "Document",
            EntityId = doc.Id.ToString(),
            Status = "Success",
            TimestampUtc = DateTime.UtcNow,
            PreviousHash = AuditService.GenesisHash,
            CurrentHash = "dummy-hash-2"
        });
        await _dbContext.SaveChangesAsync();

        // Act 3 — Second retry: audit event now exists in DB, must NOT call LogEventAsync again
        _auditMock.Reset();
        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(fileBytes));

        using var stream3 = new MemoryStream(fileBytes);
        var formFile3 = new FormFile(stream3, 0, fileBytes.Length, "file", "audit_fail.pdf");
        var result3 = await controller.MigrateLegacyDocument(doc.Id, formFile3, validHash);

        var okResult3 = result3.Should().BeOfType<OkObjectResult>().Subject;
        okResult3.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult3.Value?.ToString().Should().Contain("AlreadyMigrated = True");

        _auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentStorage",
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>()), Times.Never, "subsequent retry must NOT duplicate the audit event");
    }

    [Fact]
    public async Task MigrateLegacyDocument_ConcurrentRequests_ForSameDocument_SerializesCorrectly_AndDoesNotDuplicateAuditOrOverwriteStorage()
    {
        // Arrange
        var dbName = "RemoteStorageTest_Concurrent_" + Guid.NewGuid().ToString("N");
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var ownerId = Guid.NewGuid();
        var legacyKey = "legacy-concurrent-doc.vault";
        var fileBytes = new byte[] { 10, 20, 30, 40, 50 };
        var validHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Concurrent Doc",
            FileName = "concurrent.pdf",
            ContentType = "application/pdf",
            StorageKey = legacyKey,
            FileSizeBytes = fileBytes.Length,
            OwnerId = ownerId,
            IsDeleted = false
        };

        using (var seedContext = new ApplicationDbContext(dbOptions))
        {
            seedContext.Documents.Add(doc);
            await seedContext.SaveChangesAsync();
        }

        var newS3Key = $"vault-docs/{Guid.NewGuid():N}.vault";
        var storageMock = new Mock<IDocumentStorageService>();

        // Delay SaveDocumentAsync by 50ms so that the second request attempts to acquire the lock during migration
        storageMock.Setup(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "concurrent.pdf", "application/pdf", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(50);
                return newS3Key;
            });

        storageMock.Setup(s => s.GetDocumentStreamAsync(newS3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(fileBytes));

        var auditMock = new Mock<IAuditService>();
        // Write audit event to shared DB on successful MigrateDocumentStorage
        auditMock.Setup(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentStorage",
            "Document",
            doc.Id.ToString(),
            It.IsAny<string?>(),
            "Success",
            It.IsAny<string?>(),
            It.IsAny<string?>()))
            .Returns<Guid?, string, string, string?, string?, string, string?, string?>(async (uid, action, entityType, entityId, details, status, ip, corrId) =>
            {
                using var auditCtx = new ApplicationDbContext(dbOptions);
                var evt = new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    SequenceNumber = 1,
                    UserId = uid,
                    Action = action,
                    EntityType = entityType,
                    EntityId = entityId,
                    Details = details,
                    Status = status,
                    TimestampUtc = DateTime.UtcNow,
                    PreviousHash = AuditService.GenesisHash,
                    CurrentHash = "concurrent-audit-hash"
                };
                auditCtx.AuditEvents.Add(evt);
                await auditCtx.SaveChangesAsync();
                return evt;
            });

        var authServiceMock = new Mock<IAuthorizationService>();

        using var dbContext1 = new ApplicationDbContext(dbOptions);
        using var dbContext2 = new ApplicationDbContext(dbOptions);

        var controller1 = new DocumentsController(
            dbContext1,
            authServiceMock.Object,
            storageMock.Object,
            auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var controller2 = new DocumentsController(
            dbContext2,
            authServiceMock.Object,
            storageMock.Object,
            auditMock.Object,
            _userManagerMock.Object,
            new CreateDocumentValidator(),
            new GrantPermissionValidator(),
            new UpdateDocumentValidator());

        var ownerClaims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ownerId.ToString()),
            new(ClaimTypes.Role, Roles.Owner)
        };

        controller1.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "req-1-trace"
            }
        };

        controller2.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(ownerClaims, "Bearer")),
                TraceIdentifier = "req-2-trace"
            }
        };

        using var stream1 = new MemoryStream(fileBytes);
        var formFile1 = new FormFile(stream1, 0, fileBytes.Length, "file", "concurrent.pdf");

        using var stream2 = new MemoryStream(fileBytes);
        var formFile2 = new FormFile(stream2, 0, fileBytes.Length, "file", "concurrent.pdf");

        // Act — Execute both requests concurrently
        var task1 = controller1.MigrateLegacyDocument(doc.Id, formFile1, validHash);
        var task2 = controller2.MigrateLegacyDocument(doc.Id, formFile2, validHash);

        var results = await Task.WhenAll(task1, task2);

        // Assert — Both requests succeed with 200 OK
        results[0].Should().BeOfType<OkObjectResult>();
        results[1].Should().BeOfType<OkObjectResult>();

        var ok1 = (OkObjectResult)results[0];
        var ok2 = (OkObjectResult)results[1];
        ok1.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok2.StatusCode.Should().Be(StatusCodes.Status200OK);

        var resultTexts = new[] { ok1.Value?.ToString() ?? "", ok2.Value?.ToString() ?? "" };
        resultTexts.Should().Contain(s => s.Contains("Migrated = True"));
        resultTexts.Should().Contain(s => s.Contains("AlreadyMigrated = True"));

        // Assert 1: The stored object was saved exactly ONCE (never overwritten)
        storageMock.Verify(s => s.SaveDocumentAsync(It.IsAny<Stream>(), "concurrent.pdf", "application/pdf", It.IsAny<CancellationToken>()), Times.Once);

        // Assert 2: Final document storage key in database is correct
        using (var verifyCtx = new ApplicationDbContext(dbOptions))
        {
            var finalDoc = await verifyCtx.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
            finalDoc.StorageKey.Should().Be(newS3Key);

            // Assert 3: Only one successful migration audit event exists in the database
            var auditCount = await verifyCtx.AuditEvents.CountAsync(a =>
                a.EntityId == doc.Id.ToString() &&
                a.Action == "MigrateDocumentStorage" &&
                a.Status == "Success");
            auditCount.Should().Be(1);
        }

        // Assert 4: Audit service was invoked exactly once for MigrateDocumentStorage
        auditMock.Verify(a => a.LogEventAsync(
            ownerId,
            "MigrateDocumentStorage",
            "Document",
            doc.Id.ToString(),
            It.IsAny<string?>(),
            "Success",
            It.IsAny<string?>(),
            It.IsAny<string?>()), Times.Once);
    }

    private class FlakyTestDbContext : ApplicationDbContext
    {
        public bool DisableFailure { get; set; }

        public FlakyTestDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!DisableFailure)
            {
                throw new DbUpdateConcurrencyException("Simulated database transient write failure");
            }
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
