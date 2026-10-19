namespace TrustVault.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "TrustVault";
    public string Audience { get; set; } = "TrustVaultUsers";
    public string SecretKey { get; set; } = "TrustVaultSuperSecretKeyForDevelopment2026!MustBeAtLeast32Bytes";
    public int ExpiryMinutes { get; set; } = 60;
}
