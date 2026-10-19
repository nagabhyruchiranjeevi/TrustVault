using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Options;
using TrustVault.Api.Auth;
using TrustVault.Api.Models;
using Xunit;

namespace TrustVault.Tests;

public class TokenServiceTests
{
    private readonly TokenService _tokenService;
    private readonly JwtOptions _jwtOptions;

    public TokenServiceTests()
    {
        _jwtOptions = new JwtOptions
        {
            Issuer = "TrustVaultTest",
            Audience = "TrustVaultTestUsers",
            SecretKey = "SuperSecureSecretKeyForTestingTokensWith32Chars!",
            ExpiryMinutes = 60
        };

        var options = Options.Create(_jwtOptions);
        _tokenService = new TokenService(options);
    }

    [Fact]
    public void GenerateToken_Should_Embed_UserId_Email_And_Roles_Claims()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "jane.doe@trustvault.local",
            FullName = "Jane Doe"
        };
        var roles = new List<string> { Roles.Owner };

        // Act
        var (token, expiresAtUtc) = _tokenService.GenerateToken(user, roles);

        // Assert
        token.Should().NotBeNullOrWhiteSpace();
        expiresAtUtc.Should().BeAfter(DateTime.UtcNow);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        jwt.Issuer.Should().Be(_jwtOptions.Issuer);
        jwt.Audiences.Should().Contain(_jwtOptions.Audience);

        var subClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub);
        subClaim.Should().NotBeNull();
        subClaim!.Value.Should().Be(user.Id.ToString());

        var roleClaim = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role || c.Type == "role");
        roleClaim.Should().NotBeNull();
        roleClaim!.Value.Should().Be(Roles.Owner);
    }
}
