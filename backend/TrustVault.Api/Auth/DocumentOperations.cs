using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace TrustVault.Api.Auth;

public static class DocumentOperations
{
    public static readonly OperationAuthorizationRequirement Read = new() { Name = nameof(Read) };
    public static readonly OperationAuthorizationRequirement Edit = new() { Name = nameof(Edit) };
    public static readonly OperationAuthorizationRequirement Delete = new() { Name = nameof(Delete) };
    public static readonly OperationAuthorizationRequirement Share = new() { Name = nameof(Share) };
}
