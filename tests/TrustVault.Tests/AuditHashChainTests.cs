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

    [Fact]
    public void HashChain_ComputeDeterministicHash_Should_Produce_Consistent_Deterministic_Results()
    {
        // Arrange
        var prev = "0000000000000000000000000000000000000000000000000000000000000000";
        var seq = 1L;
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var ts = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var uid = Guid.Parse("22222222-2222-2222-2222-222222222222");

        // Act
        var hash1 = AuditEvent.ComputeDeterministicHash(prev, seq, id, ts, uid, "Upload", "Document", "doc-1", "Success", "Detail", "corr-1");
        var hash2 = AuditEvent.ComputeDeterministicHash(prev, seq, id, ts, uid, "Upload", "Document", "doc-1", "Success", "Detail", "corr-1");

        // Assert
        hash1.Should().NotBeNullOrWhiteSpace();
        hash1.Should().Be(hash2, "deterministic hash computation must yield identical output across identical inputs");
        hash1.Length.Should().Be(64, "SHA-256 hex string should be 64 characters");
    }

    [Fact]
    public async Task HashChain_Should_Handle_Concurrent_Writes_Safely_And_Preserve_Chain_Integrity()
    {
        // Arrange - Spawn 10 parallel tasks logging events concurrently
        var tasks = Enumerable.Range(1, 10).Select(i =>
        {
            var user = Guid.NewGuid();
            return _auditService.LogEventAsync(user, $"ConcurrentAction_{i}", "TestEntity", i.ToString(), $"Concurrent event {i}", "Success");
        });

        // Act
        var loggedEvents = await Task.WhenAll(tasks);

        // Assert
        loggedEvents.Should().HaveCount(10);
        var seqNumbers = loggedEvents.Select(e => e.SequenceNumber).OrderBy(s => s).ToList();
        seqNumbers.Should().OnlyHaveUniqueItems("each concurrent write must receive a strictly unique sequence number");
        seqNumbers.Should().Equal(Enumerable.Range(1, 10).Select(i => (long)i), "sequence numbers should be contiguous from 1 to 10");

        var (isValid, violation, count) = await _auditService.VerifyChainIntegrityAsync();
        isValid.Should().BeTrue("the resulting hash chain from concurrent writes must remain unbroken and valid");
        violation.Should().BeNull();
        count.Should().Be(10);
    }
}
