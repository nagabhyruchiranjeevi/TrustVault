using Amazon.S3;
using Amazon.S3.Model;

namespace TrustVault.Api.Services;

public interface IDocumentStorageService
{
    Task<string> SaveDocumentAsync(byte[] content, string originalFileName, string contentType, CancellationToken cancellationToken = default);
    Task<string> SaveDocumentAsync(Stream contentStream, string originalFileName, string contentType, CancellationToken cancellationToken = default);
    Task<Stream> GetDocumentStreamAsync(string storageKey, CancellationToken cancellationToken = default);
    Task<byte[]> GetDocumentBytesAsync(string storageKey, CancellationToken cancellationToken = default);
    Task DeleteDocumentAsync(string storageKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Secure local filesystem storage for development environments.
/// Keeps documents isolated outside wwwroot with cryptographically random keys and path traversal guards.
/// </summary>
public class LocalSecureFileStorageService : IDocumentStorageService
{
    private readonly string _storageDirectory;
    private readonly string? _fallbackDirectory;
    private readonly ILogger<LocalSecureFileStorageService> _logger;

    public LocalSecureFileStorageService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<LocalSecureFileStorageService> logger)
    {
        _logger = logger;
        var configuredPath = configuration["Storage:LocalVaultPath"]
            ?? Environment.GetEnvironmentVariable("STORAGE_LOCAL_VAULT_PATH")
            ?? "storage";

        var targetPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        (_storageDirectory, _fallbackDirectory) = ResolveStorageDirectories(targetPath, environment.ContentRootPath, logger);
    }

    private static (string Primary, string? Fallback) ResolveStorageDirectories(string preferredPath, string contentRootPath, ILogger logger)
    {
        // 1. Try preferred path
        if (TryEnsureDirectoryWritable(preferredPath, out var err1))
        {
            return (preferredPath, null);
        }

        logger.LogWarning(
            "Configured local storage directory '{Path}' is not writable ({Error}). Attempting fallback storage locations.",
            preferredPath, err1);

        // 2. Try container storage directory (/app/storage) pre-created for non-root containers in Dockerfile
        var containerStorage = Path.Combine(contentRootPath, "storage");
        if (TryEnsureDirectoryWritable(containerStorage, out var err2))
        {
            logger.LogInformation("Active storage provider: Local filesystem at '{Path}'. Note: Container storage is ephemeral on cloud hosts like Render.", containerStorage);
            return (containerStorage, preferredPath);
        }

        // 3. Fallback to system temporary directory
        var tempStorage = Path.Combine(Path.GetTempPath(), "TrustVaultStorage");
        if (TryEnsureDirectoryWritable(tempStorage, out var err3))
        {
            logger.LogWarning("Active storage provider: Local temporary directory at '{Path}'. Note: Storage is ephemeral and not durable across restarts.", tempStorage);
            return (tempStorage, preferredPath);
        }

        throw new InvalidOperationException(
            $"Cannot initialize local storage: neither '{preferredPath}', '{containerStorage}', nor '{tempStorage}' is writable.");
    }

    private static bool TryEnsureDirectoryWritable(string path, out string? error)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var probeFile = Path.Combine(path, $".probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "probe");
            File.Delete(probeFile);

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public async Task<string> SaveDocumentAsync(byte[] content, string originalFileName, string contentType, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(content);
        return await SaveDocumentAsync(stream, originalFileName, contentType, cancellationToken);
    }

    public async Task<string> SaveDocumentAsync(Stream contentStream, string originalFileName, string contentType, CancellationToken cancellationToken = default)
    {
        // Safe randomized key completely decoupled from user-supplied name
        var safeKey = $"{Guid.NewGuid():N}.vault";
        var targetPath = Path.Combine(_storageDirectory, safeKey);

        await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await contentStream.CopyToAsync(fileStream, cancellationToken);

        _logger.LogInformation("Saved document to local secure vault storage key: {Key}", safeKey);
        return safeKey;
    }

    public Task<Stream> GetDocumentStreamAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var safeKey = Path.GetFileName(storageKey);
        var targetPath = Path.Combine(_storageDirectory, safeKey);

        if (!File.Exists(targetPath) && _fallbackDirectory != null)
        {
            var fallbackPath = Path.Combine(_fallbackDirectory, safeKey);
            if (File.Exists(fallbackPath))
            {
                targetPath = fallbackPath;
            }
        }

        if (!File.Exists(targetPath))
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "TrustVaultStorage", safeKey);
            if (File.Exists(tempPath))
            {
                targetPath = tempPath;
            }
            else
            {
                throw new FileNotFoundException("Requested secure document binary could not be found.");
            }
        }

        Stream stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return Task.FromResult(stream);
    }

    public async Task<byte[]> GetDocumentBytesAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        await using var stream = await GetDocumentStreamAsync(storageKey, cancellationToken);
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        return memoryStream.ToArray();
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
        else if (_fallbackDirectory != null)
        {
            var fallbackPath = Path.Combine(_fallbackDirectory, safeKey);
            if (File.Exists(fallbackPath))
            {
                File.Delete(fallbackPath);
                _logger.LogInformation("Secure storage fallback file deleted: {Key}", safeKey);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Secure Amazon S3 storage provider for staging and production cloud deployments.
/// Uses a private S3 bucket without public access. All downloads are authorized and streamed
/// through the backend to guarantee instant access revocation enforcement.
/// </summary>
public class S3DocumentStorageService : IDocumentStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly bool _enableServerSideEncryption;
    private readonly string? _legacyLocalDirectory;
    private readonly ILogger<S3DocumentStorageService> _logger;

    public S3DocumentStorageService(
        IAmazonS3 s3Client,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<S3DocumentStorageService> logger)
    {
        _s3Client = s3Client;
        _logger = logger;
        _bucketName = configuration["Storage:S3BucketName"]
            ?? Environment.GetEnvironmentVariable("STORAGE_S3_BUCKET_NAME")
            ?? "trustvault-private-vault";

        // Supabase Storage, Cloudflare R2, and MinIO do not accept AWS SSE headers.
        // ServerSideEncryption is only enabled if explicitly configured.
        _enableServerSideEncryption = configuration.GetValue<bool>("Storage:EnableS3ServerSideEncryption", false);

        // Optional legacy fallback directory for reading existing local documents
        var localPath = configuration["Storage:LocalVaultPath"]
            ?? Environment.GetEnvironmentVariable("STORAGE_LOCAL_VAULT_PATH")
            ?? "storage";
        var resolvedLocal = Path.IsPathRooted(localPath) ? localPath : Path.Combine(environment.ContentRootPath, localPath);
        _legacyLocalDirectory = Directory.Exists(resolvedLocal) ? resolvedLocal : null;
    }

    public async Task<string> SaveDocumentAsync(byte[] content, string originalFileName, string contentType, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(content);
        return await SaveDocumentAsync(stream, originalFileName, contentType, cancellationToken);
    }

    public async Task<string> SaveDocumentAsync(Stream contentStream, string originalFileName, string contentType, CancellationToken cancellationToken = default)
    {
        var safeKey = $"vault-docs/{Guid.NewGuid():N}.vault";

        var putRequest = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = safeKey,
            InputStream = contentStream,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
        };

        if (_enableServerSideEncryption)
        {
            putRequest.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

        await _s3Client.PutObjectAsync(putRequest, cancellationToken);
        _logger.LogInformation("Stored document in private S3 bucket {Bucket} with key {Key}", _bucketName, safeKey);

        return safeKey;
    }

    /// <summary>
    /// Positively determines whether a storage key represents a legacy local-only document.
    /// Remote S3/Supabase storage objects are strictly namespaced under 'vault-docs/' (e.g. 'vault-docs/{guid}.vault').
    /// Legacy local documents are flat filenames without directories (e.g. '{guid}.vault').
    /// </summary>
    public static bool IsLegacyLocalKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey)) return false;
        return !storageKey.StartsWith("vault-docs/", StringComparison.OrdinalIgnoreCase) &&
               !storageKey.Contains('/') &&
               !storageKey.Contains('\\');
    }

    public async Task<Stream> GetDocumentStreamAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var getRequest = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = storageKey
            };

            var response = await _s3Client.GetObjectAsync(getRequest, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            // Fall back to legacy local directory ONLY if this key is positively established as a legacy local-only document.
            // Network outages, timeouts, and auth errors propagate as exceptions and never fall back to local disk.
            // S3-namespaced documents ('vault-docs/*') are never served from local disk even on 404.
            if (IsLegacyLocalKey(storageKey) && _legacyLocalDirectory != null)
            {
                var safeKey = Path.GetFileName(storageKey);
                var localPath = Path.Combine(_legacyLocalDirectory, safeKey);
                if (File.Exists(localPath))
                {
                    _logger.LogInformation("Serving verified legacy document from local storage for key {Key}", safeKey);
                    Stream stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                    return stream;
                }
            }

            throw new FileNotFoundException($"Object '{storageKey}' not found in remote storage bucket '{_bucketName}'.", ex);
        }
    }

    public async Task<byte[]> GetDocumentBytesAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        await using var stream = await GetDocumentStreamAsync(storageKey, cancellationToken);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }

    public async Task DeleteDocumentAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        // 1. Delete from remote S3/Supabase storage. If the remote delete fails, do NOT swallow the error.
        var deleteRequest = new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = storageKey
        };

        await _s3Client.DeleteObjectAsync(deleteRequest, cancellationToken);
        _logger.LogInformation("Deleted document from private S3 bucket {Bucket} with key {Key}", _bucketName, storageKey);

        // 2. Clean up legacy local file if it existed. Do not silently swallow local delete errors.
        if (_legacyLocalDirectory != null)
        {
            var safeKey = Path.GetFileName(storageKey);
            var localPath = Path.Combine(_legacyLocalDirectory, safeKey);
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
                _logger.LogInformation("Deleted legacy local file for key {Key}", safeKey);
            }
        }
    }
}
