using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using TrustVault.Api.Controllers;
using TrustVault.Api.DTOs;
using TrustVault.Api.Models;
using TrustVault.Api.Services;
using TrustVault.Api.Validators;
using Xunit;

namespace TrustVault.Tests;

public class AuthSecurityTests
{
    private readonly Mock<IAuthService> _authServiceMock;
    private readonly AuthController _authController;

    public AuthSecurityTests()
    {
        _authServiceMock = new Mock<IAuthService>();
        var registerValidator = new RegisterRequestValidator();
        var loginValidator = new LoginRequestValidator();

        _authController = new AuthController(_authServiceMock.Object, registerValidator, loginValidator);
    }

    [Fact]
    public async Task GetMe_Without_User_Claim_Should_Return_Unauthorized()
    {
        // Arrange - empty identity (unauthenticated)
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        _authController.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        var result = await _authController.GetMe();

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task GetMe_With_Valid_Claim_Should_Return_User_Profile()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Email, "alice@trustvault.local")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        _authController.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var expectedUser = new UserDto(userId, "alice@trustvault.local", "Alice Smith", new[] { Roles.Owner }, DateTime.UtcNow);
        _authServiceMock.Setup(s => s.GetUserByIdAsync(userId)).ReturnsAsync(expectedUser);

        // Act
        var result = await _authController.GetMe();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedUser = okResult.Value.Should().BeOfType<UserDto>().Subject;
        returnedUser.Email.Should().Be("alice@trustvault.local");
        returnedUser.Roles.Should().Contain(Roles.Owner);
    }
}
