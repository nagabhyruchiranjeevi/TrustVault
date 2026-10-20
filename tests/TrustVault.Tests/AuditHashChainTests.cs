using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using TrustVault.Api.Data;
using TrustVault.Api.Models;
using TrustVault.Api.Services;
using Xunit;

namespace TrustVault.Tests;

public class AuditHashChainTests
{
    private readonly ApplicationDbContext _dbContext;
    private readonly AuditService _auditService;

    public AuditHashChainTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new ApplicationDbContext(options);
        var loggerMock = new Mock<ILogger<AuditService>>();
        _auditService = new AuditService(_dbContext, loggerMock.Object);
    }

    [Fact]
    public async Task HashChain_Should_Verify_Successfully_For_Valid_Events()
    {
        // Arrange - Log 5 sequential audit events
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        await _auditService.LogEventAsync(user1, "Login", "User", user1.ToString(), "User login", "Success");
        await _auditService.LogEventAsync(user1, "UploadDocument", "Document", Guid.NewGuid().ToString(), "Uploaded file.pdf", "Success");
        await _auditService.LogEventAsync(user1, "GrantPermission", "DocumentPermission", user2.ToString(), "Granted Read", "Success");
        await _auditService.LogEventAsync(user2, "DownloadDocument", "Document", Guid.NewGuid().ToString(), "Downloaded file.pdf", "Success");
        await _auditService.LogEventAsync(user1, "RevokePermission", "DocumentPermission", user2.ToString(), "Revoked Read", "Success");

        // Act
        var (isValid, violation, count) = await _auditService.VerifyChainIntegrityAsync();

        // Assert
        isValid.Should().BeTrue("a genuine undisturbed hash chain must pass verification");
        violation.Should().BeNull();
        count.Should().Be(5);
    }

    [Fact]
    public async Task HashChain_Should_Detect_Tampering_When_Event_Details_Are_Modified()
    {
        // Arrange - Create 3 events
        var user = Guid.NewGuid();
        await _auditService.LogEventAsync(user, "Action1", "Entity", "1", "Original Detail 1", "Success");
        var targetEvent = await _auditService.LogEventAsync(user, "Action2", "Entity", "2", "Original Detail 2", "Success");
        await _auditService.LogEventAsync(user, "Action3", "Entity", "3", "Original Detail 3", "Success");

        // Act - Simulate a malicious database tampering event
        var tracked = await _dbContext.AuditEvents.FirstAsync(a => a.Id == targetEvent.Id);
        tracked.Details = "TAMPERED: Attacker modified this audit record directly in DB!";
        await _dbContext.SaveChangesAsync();

        var (isValid, violation, count) = await _auditService.VerifyChainIntegrityAsync();

        // Assert
        isValid.Should().BeFalse("hash chain must catch altered data in historical records");
        violation.Should().Contain("Data tampering detected at Sequence #2");
        count.Should().Be(1, "it should have stopped at the second event");
    }

    [Fact]
    public async Task HashChain_Should_Detect_Tampering_When_PreviousHash_Is_Broken()
    {
        // Arrange - Create 3 events
        var user = Guid.NewGuid();
        await _auditService.LogEventAsync(user, "Action1", "Entity", "1", "Detail 1", "Success");
        await _auditService.LogEventAsync(user, "Action2", "Entity", "2", "Detail 2", "Success");
        var thirdEvent = await _auditService.LogEventAsync(user, "Action3", "Entity", "3", "Detail 3", "Success");

        // Act - Tamper with the chain link
        var tracked = await _dbContext.AuditEvents.FirstAsync(a => a.Id == thirdEvent.Id);
        tracked.PreviousHash = "000000000000000000000000000000000000000000000000000000000000bad1";
        await _dbContext.SaveChangesAsync();

        var (isValid, violation, count) = await _auditService.VerifyChainIntegrityAsync();

        // Assert
        isValid.Should().BeFalse("hash chain must detect broken linkage");
        violation.Should().Contain("Hash chain broken at Sequence #3");
        count.Should().Be(2);
    }
}
