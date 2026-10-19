namespace TrustVault.Api.Services;

public interface IDocumentStorageService
{
    Task<string> SaveDocumentAsync(byte[] content, string originalFileName, CancellationToken cancellationToken = default);
    Task<byte[]> GetDocumentBytesAsync(string storageKey, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(string storageKey, CancellationToken cancellationToken = default);
}

public class LocalSecureFileStorageService : IDocumentStorageService
{
    private readonly string _storageDirectory;
    private readonly ILogger<LocalSecureFileStorageService> _logger;

    public LocalSecureFileStorageService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<LocalSecureFileStorageService> logger)
    {
        _logger = logger;
        // Non-public path outside of wwwroot
        var configuredPath = configuration["Storage:LocalVaultPath"] ?? "SecureVaultStorage";
        _storageDirectory = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }
    }

    public async Task<string> SaveDocumentAsync(byte[] content, string originalFileName, CancellationToken cancellationToken = default)
    {
        // Generate an unguessable storage key (Guid + random suffix) to decouple from original filename
        var safeStorageKey = $"{Guid.NewGuid():N}.vault";
        var targetPath = Path.Combine(_storageDirectory, safeStorageKey);

        await File.WriteAllBytesAsync(targetPath, content, cancellationToken);
        _logger.LogInformation("Saved encrypted document binary to secure storage key: {Key}", safeStorageKey);

        return safeStorageKey;
    }

    public async Task<byte[]> GetDocumentBytesAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        // Enforce filename safety to prevent path traversal
        var safeKey = Path.GetFileName(storageKey);
        var targetPath = Path.Combine(_storageDirectory, safeKey);

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("Requested secure document binary could not be found.");
        }

        return await File.ReadAllBytesAsync(targetPath, cancellationToken);
    }

    public Task DeleteDocumentAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var safeKey = Path.GetFileName(storageKey);
        var targetPath = Path.Combine(_storageDirectory, safeKey);

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
            _logger.LogInformation("Secure storage file deleted: {Key}", safeKey);
        }

        return Task.CompletedTask;
    }
}
