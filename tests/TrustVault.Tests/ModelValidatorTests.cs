using FluentAssertions;
using TrustVault.Api.DTOs;
using TrustVault.Api.Validators;
using Xunit;

namespace TrustVault.Tests;

public class ModelValidatorTests
{
    private readonly RegisterRequestValidator _registerValidator = new();
    private readonly CreateDocumentValidator _documentValidator = new();

    [Theory]
    [InlineData("short", false)] // too short
    [InlineData("alllowercase123!", false)] // no uppercase
    [InlineData("ALLUPPERCASE123!", false)] // no lowercase
    [InlineData("NoNumberAtAll!", false)] // no digit
    [InlineData("NoSpecialChar123", false)] // no special character
    [InlineData("ValidP@ssw0rd!", true)] // valid
    public void RegisterValidator_PasswordStrength_Rules(string password, bool expectedValid)
    {
        var request = new RegisterRequest("user@example.com", password, "Test User", "Viewer");
        var result = _registerValidator.Validate(request);

        result.IsValid.Should().Be(expectedValid);
    }

    [Fact]
    public void RegisterValidator_Should_Reject_Self_Registration_As_Admin()
    {
        var request = new RegisterRequest("hacker@example.com", "Str0ng!Pass", "Hacker", "Admin");
        var result = _registerValidator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Role");
    }

    [Theory]
    [InlineData("../../../etc/passwd", false)]
    [InlineData("..\\windows\\system32\\cmd.exe", false)]
    [InlineData("valid_document.pdf", true)]
    [InlineData("monthly_report_2026.docx", true)]
    public void CreateDocumentValidator_Should_Block_PathTraversal_Sequences(string fileName, bool expectedValid)
    {
        var request = new CreateDocumentDto("Quarterly Report", fileName, "application/pdf", "dGVzdCBjb250ZW50");
        var result = _documentValidator.Validate(request);

        result.IsValid.Should().Be(expectedValid);
    }
}
