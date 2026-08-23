using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace UttorAI.Api.Services.Storage;

/// <summary>Result of the startup connectivity probe, so a storage misconfiguration shows up in
/// the UI instead of only appearing as a failed upload later.</summary>
public class StorageHealth
{
    public bool Healthy { get; set; } = true;
    public string? Error { get; set; }
}

public class StorageOptions
{
    /// <summary>Local or AzureBlob.</summary>
    public string Provider { get; set; } = "Local";
    public string LocalRoot { get; set; } = "";
    public AzureBlobOptions Azure { get; set; } = new();
}

public class AzureBlobOptions
{
    /// <summary>Full connection string. Takes priority when set.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Storage account name, used with Entra ID credentials when no connection string is
    /// configured. This is the passwordless path and the one to prefer in Azure.</summary>
    public string AccountName { get; set; } = "";

    /// <summary>Single: one container, with a folder per tenant inside it.
    /// PerTenant: a dedicated container per tenant, which allows container-scoped keys and RBAC.</summary>
    public string ContainerStrategy { get; set; } = "Single";

    /// <summary>Container name for the Single strategy, or the prefix for PerTenant containers.</summary>
    public string Container { get; set; } = "uttorai-documents";

    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString)
                           || !string.IsNullOrWhiteSpace(AccountName);
}

/// <summary>Azure Blob Storage implementation of <see cref="IDocumentStorage"/>.
///
/// Tenant separation is structural: every blob name begins with the tenant id, so a tenant's
/// documents occupy their own folder (Single strategy) or their own container (PerTenant). Nothing
/// in this class chooses the tenant — it comes from the caller's JWT, so a request cannot reach
/// another tenant's prefix.</summary>
public class AzureBlobStorage : IDocumentStorage
{
    private readonly BlobServiceClient _service;
    private readonly AzureBlobOptions _options;
    private readonly ITenantSlugResolver _slugs;
    private readonly ILogger<AzureBlobStorage> _logger;
    private readonly HashSet<string> _ensuredContainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _containerGate = new(1, 1);

    public AzureBlobStorage(StorageOptions options, ITenantSlugResolver slugs,
        ILogger<AzureBlobStorage> logger)
    {
        _options = options.Azure;
        _slugs = slugs;
        _logger = logger;

        _service = string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? new BlobServiceClient(new Uri($"https://{_options.AccountName}.blob.core.windows.net"),
                new DefaultAzureCredential())
            : new BlobServiceClient(_options.ConnectionString);
    }

    private bool PerTenant =>
        _options.ContainerStrategy.Equals("PerTenant", StringComparison.OrdinalIgnoreCase);

    public string ProviderName => PerTenant
        ? $"Azure Blob ({_service.AccountName}, container per tenant)"
        : $"Azure Blob ({_service.AccountName}/{_options.Container}, folder per tenant)";

    /// <summary>Verifies the account is reachable and the shared container exists. Called at
    /// startup so a misconfigured account fails loudly rather than at first upload.</summary>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (PerTenant)
        {
            // Containers are created per tenant on first write; just prove the account answers.
            await _service.GetPropertiesAsync(ct);
            _logger.LogInformation("Azure Blob ready on account {Account} (container per tenant)",
                _service.AccountName);
            return;
        }

        var container = _service.GetBlobContainerClient(_options.Container);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        _logger.LogInformation("Azure Blob ready: {Account}/{Container}",
            _service.AccountName, _options.Container);
    }

    // ------------------------------ key resolution ------------------------------

    /// <summary>Maps a storage key onto a container plus a blob name. The key itself is identical
    /// under both strategies, so switching strategy does not invalidate stored keys — only where
    /// the bytes live.</summary>
    private (BlobContainerClient container, string blobName) Resolve(string storageKey)
    {
        if (!PerTenant)
            return (_service.GetBlobContainerClient(_options.Container), storageKey);

        var (tenant, remainder) = StorageKey.SplitTenant(storageKey);
        return (_service.GetBlobContainerClient(ContainerFor(tenant)), remainder);
    }

    /// <summary>Builds the container name for a tenant, preferring the tenant slug so the account
    /// is readable in Storage Explorer (<c>uttorai-documents-contoso</c> rather than a raw guid).
    /// Falls back to the guid when the slug cannot be resolved, so a blob is never unreachable.
    ///
    /// The slug is safe to build a name from because this API never lets a tenant change it — only
    /// the display name is editable.</summary>
    private string ContainerFor(string tenantId)
    {
        var prefix = _options.Container.Trim('-').ToLowerInvariant();

        var slug = Guid.TryParse(tenantId, out var id) ? _slugs.Resolve(id) : null;
        var suffix = string.IsNullOrWhiteSpace(slug)
            ? tenantId.Replace("-", "").ToLowerInvariant()
            : slug;

        return SanitiseContainerName($"{prefix}-{suffix}");
    }

    /// <summary>Azure allows 3-63 characters, lower-case letters, digits and single hyphens, and
    /// the name must start and end alphanumeric.</summary>
    internal static string SanitiseContainerName(string raw)
    {
        var cleaned = new string(raw.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());

        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        cleaned = cleaned.Trim('-');

        if (cleaned.Length > 63) cleaned = cleaned[..63].Trim('-');
        if (cleaned.Length < 3) cleaned = $"{cleaned}-docs".Trim('-');
        return cleaned;
    }

    /// <summary>Creates a container for each existing tenant so they are visible in the portal
    /// immediately, rather than only appearing after that tenant's first upload.</summary>
    public async Task EnsureTenantContainersAsync(IEnumerable<Guid> tenantIds, CancellationToken ct = default)
    {
        if (!PerTenant) return;
        foreach (var id in tenantIds)
        {
            var container = _service.GetBlobContainerClient(ContainerFor(id.ToString()));
            await EnsureContainerAsync(container, ct);
            _logger.LogInformation("Tenant container ready: {Container}", container.Name);
        }
    }

    private async Task EnsureContainerAsync(BlobContainerClient container, CancellationToken ct)
    {
        if (_ensuredContainers.Contains(container.Name)) return;

        await _containerGate.WaitAsync(ct);
        try
        {
            if (_ensuredContainers.Contains(container.Name)) return;
            await CreateWithRetryAsync(container, ct);
            _ensuredContainers.Add(container.Name);
        }
        finally
        {
            _containerGate.Release();
        }
    }

    /// <summary>Azure deletes a container asynchronously and refuses to recreate the same name until
    /// it finishes, which usually takes under a minute. That window is reachable in normal use — a
    /// workspace is deleted and another is created with the same name — so wait it out rather than
    /// failing the upload.</summary>
    private async Task CreateWithRetryAsync(BlobContainerClient container, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var deadline = DateTime.UtcNow.AddSeconds(45);

        while (true)
        {
            try
            {
                await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == "ContainerBeingDeleted"
                                                    && DateTime.UtcNow < deadline)
            {
                _logger.LogInformation(
                    "Container {Container} is still being deleted; retrying in {Delay}s",
                    container.Name, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.6, 10));
            }
        }
    }

    // ---------------------------------- operations ------------------------------

    public async Task<string> SaveAsync(Guid tenantId, Guid knowledgeBaseId, string fileName,
        Stream content, CancellationToken ct = default)
    {
        var key = StorageKey.Build(tenantId, knowledgeBaseId, fileName);
        var (container, blobName) = Resolve(key);
        await EnsureContainerAsync(container, ct);

        if (content.CanSeek) content.Position = 0;

        var blob = container.GetBlobClient(blobName);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = GuessContentType(fileName) },
            // The tenant is recorded on the blob itself, so an operator inspecting the account can
            // tell who owns a blob without consulting the database.
            Metadata = new Dictionary<string, string>
            {
                ["tenantId"] = tenantId.ToString(),
                ["knowledgeBaseId"] = knowledgeBaseId.ToString(),
                ["originalFileName"] = SanitiseMetadata(fileName),
                ["uploadedUtc"] = DateTime.UtcNow.ToString("O")
            }
        }, ct);

        return key;
    }

    /// <summary>Writes content at an existing key instead of generating a new one. Used only by
    /// StorageMigrator, which must keep the keys already recorded in the database.</summary>
    public async Task UploadAtKeyAsync(string storageKey, Stream content, CancellationToken ct = default)
    {
        var (container, blobName) = Resolve(storageKey);
        await EnsureContainerAsync(container, ct);
        if (content.CanSeek) content.Position = 0;

        await container.GetBlobClient(blobName).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = GuessContentType(blobName) },
            Metadata = new Dictionary<string, string> { ["migratedUtc"] = DateTime.UtcNow.ToString("O") }
        }, ct);
    }

    public async Task<Stream> OpenAsync(string storageKey, CancellationToken ct = default)
    {
        var (container, blobName) = Resolve(storageKey);
        var response = await container.GetBlobClient(blobName).DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var (container, blobName) = Resolve(storageKey);
        try
        {
            await container.GetBlobClient(blobName)
                .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Container already gone; nothing to remove.
        }
    }

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default)
    {
        var (container, blobName) = Resolve(storageKey);
        try
        {
            return await container.GetBlobClient(blobName).ExistsAsync(ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Per-tenant containers are dropped whole; in the shared container every blob under
    /// the tenant's prefix is removed.</summary>
    public async Task DeleteTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (PerTenant)
        {
            var container = _service.GetBlobContainerClient(ContainerFor(tenantId.ToString()));

            // Delete the blobs first: that removes the customer's content immediately and
            // synchronously. Dropping the container afterwards is tidiness, and is the part Azure
            // completes in its own time.
            var removed = 0;
            try
            {
                await foreach (var blob in container.GetBlobsAsync(cancellationToken: ct))
                {
                    await container.DeleteBlobIfExistsAsync(blob.Name,
                        DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
                    removed++;
                }
                await container.DeleteIfExistsAsync(cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Never existed, or already gone.
            }

            await _containerGate.WaitAsync(ct);
            try { _ensuredContainers.Remove(container.Name); }
            finally { _containerGate.Release(); }

            _logger.LogInformation("Purged {Count} blob(s) and deleted container {Container}",
                removed, container.Name);
            return;
        }

        var shared = _service.GetBlobContainerClient(_options.Container);
        var prefix = $"{tenantId}/";
        var deleted = 0;
        await foreach (var blob in shared.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            await shared.DeleteBlobIfExistsAsync(blob.Name,
                DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            deleted++;
        }
        _logger.LogInformation("Deleted {Count} blob(s) under {Prefix}", deleted, prefix);
    }

    private static string SanitiseMetadata(string value)
    {
        // Blob metadata values must be ASCII; non-Latin file names would otherwise fail the upload.
        var ascii = new string(value.Where(c => c is >= (char)32 and <= (char)126).ToArray());
        return string.IsNullOrWhiteSpace(ascii) ? "file" : ascii;
    }

    private static string GuessContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" or ".log" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            _ => "application/octet-stream"
        };
}
