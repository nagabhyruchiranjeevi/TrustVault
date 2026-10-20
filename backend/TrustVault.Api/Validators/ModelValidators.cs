using FluentValidation;
using TrustVault.Api.DTOs;
using TrustVault.Api.Models;

namespace TrustVault.Api.Validators;

public static class SecurityConstants
{
    public const long MaxFileSizeBytes = 15 * 1024 * 1024; // 15 Megabytes

    public static readonly HashSet<string> PermittedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".txt", ".docx", ".xlsx", ".csv", ".json", ".xml"
    };

    public static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1", ".vbs", ".js", ".msi", ".jar", ".scr", ".pif"
    };
}

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
            .MaximumLength(255).WithMessage("Title cannot exceed 255 characters.");

        RuleFor(x => x.FileName)
            .NotEmpty().WithMessage("File name is required.")
            .MaximumLength(255).WithMessage("File name cannot exceed 255 characters.")
            .Must(fn => !fn.Contains("..") && !fn.Contains('/') && !fn.Contains('\\'))
            .WithMessage("File name contains invalid or path-traversal characters.")
            .Must(fn =>
            {
                var ext = Path.GetExtension(fn);
                return !string.IsNullOrEmpty(ext) && SecurityConstants.PermittedExtensions.Contains(ext);
            })
            .WithMessage("File extension is not permitted. Allowed types: PDF, PNG, JPG, TXT, DOCX, XLSX, CSV, JSON, XML.")
            .Must(fn =>
            {
                var ext = Path.GetExtension(fn);
                return string.IsNullOrEmpty(ext) || !SecurityConstants.BlockedExtensions.Contains(ext);
            })
            .WithMessage("Executable and script files are strictly prohibited.");

        RuleFor(x => x.Base64Content)
            .NotEmpty().WithMessage("Document content is required.")
            .Must(base64 =>
            {
                if (string.IsNullOrEmpty(base64)) return false;
                // Approximate base64 decoded size: (length * 3 / 4)
                var approximateSize = (base64.Length * 3) / 4;
                return approximateSize <= SecurityConstants.MaxFileSizeBytes;
            })
            .WithMessage($"Document payload exceeds the maximum allowed size of {SecurityConstants.MaxFileSizeBytes / (1024 * 1024)}MB.");
    }
}

public class UpdateDocumentValidator : AbstractValidator<UpdateDocumentDto>
{
    public UpdateDocumentValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Document title is required.")
            .MaximumLength(255).WithMessage("Document title cannot exceed 255 characters.");
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
