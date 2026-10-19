using FluentValidation;
using TrustVault.Api.DTOs;
using TrustVault.Api.Models;

namespace TrustVault.Api.Validators;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.")
            .MaximumLength(256);

        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full Name is required.")
            .MaximumLength(150);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.")
            .Matches(@"[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches(@"[a-z]").WithMessage("Password must contain at least one lowercase letter.")
            .Matches(@"[0-9]").WithMessage("Password must contain at least one number.")
            .Matches(@"[\!\?\*\@\#\$\%\^\&\+\=]").WithMessage("Password must contain at least one special character.");

        // Security rule: Admin cannot be self-assigned through public registration
        RuleFor(x => x.Role)
            .Must(role => string.IsNullOrEmpty(role) || role == Roles.Owner || role == Roles.Viewer)
            .WithMessage("Invalid role selection. Admin roles cannot be self-assigned.");
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email address is required.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}

public class CreateDocumentValidator : AbstractValidator<CreateDocumentDto>
{
    public CreateDocumentValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Document title is required.")
            .MaximumLength(255);

        RuleFor(x => x.FileName)
            .NotEmpty().WithMessage("File name is required.")
            .MaximumLength(255)
            .Must(fn => !fn.Contains("..") && !fn.Contains('/') && !fn.Contains('\\'))
            .WithMessage("File name contains invalid or path-traversal characters.");

        RuleFor(x => x.Base64Content)
            .NotEmpty().WithMessage("Document content is required.");
    }
}

public class GrantPermissionValidator : AbstractValidator<GrantPermissionDto>
{
    public GrantPermissionValidator()
    {
        RuleFor(x => x.UserEmail)
            .NotEmpty().WithMessage("User email is required.")
            .EmailAddress().WithMessage("Valid email required.");

        RuleFor(x => x.PermissionLevel)
            .IsInEnum().WithMessage("Valid permission level (Read or Edit) required.");
    }
}
