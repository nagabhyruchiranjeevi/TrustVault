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
    private readonly ILogger<LocalSecureFileStorageService> _logger;

    public LocalSecureFileStorageService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<LocalSecureFileStorageService> logger)
    {
        _logger = logger;
        var configuredPath = configuration["Storage:LocalVaultPath"] ?? "SecureVaultStorage";
        _storageDirectory = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
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

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("Requested secure document binary could not be found.");
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
    private readonly ILogger<S3DocumentStorageService> _logger;

    public S3DocumentStorageService(IAmazonS3 s3Client, IConfiguration configuration, ILogger<S3DocumentStorageService> logger)
    {
        _s3Client = s3Client;
        _logger = logger;
        _bucketName = configuration["Storage:S3BucketName"] ?? "trustvault-private-vault";
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
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256 // At-rest S3 encryption
        };

        await _s3Client.PutObjectAsync(putRequest, cancellationToken);
        _logger.LogInformation("Stored document in private S3 bucket {Bucket} with key {Key}", _bucketName, safeKey);

        return safeKey;
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
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Object {storageKey} not found in S3 bucket {_bucketName}.", ex);
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
        var deleteRequest = new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = storageKey
        };

        await _s3Client.DeleteObjectAsync(deleteRequest, cancellationToken);
        _logger.LogInformation("Deleted document from private S3 bucket {Bucket} with key {Key}", _bucketName, storageKey);
    }
}
