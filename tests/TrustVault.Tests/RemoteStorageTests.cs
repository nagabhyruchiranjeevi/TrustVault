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
