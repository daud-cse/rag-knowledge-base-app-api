namespace UttorAI.Api.Services.Storage;

/// <summary>Binary document store.
///
/// Storage keys always have the shape <c>{tenantId}/{knowledgeBaseId}/{unique}_{fileName}</c>.
/// On disk that is a directory tree; in Azure Blob Storage the same string is a blob name, and the
/// slashes render as folders in Storage Explorer and the portal. Keeping one key shape means the
/// provider can be swapped without touching a single stored row.</summary>
public interface IDocumentStorage
{
    string ProviderName { get; }

    Task<string> SaveAsync(Guid tenantId, Guid knowledgeBaseId, string fileName, Stream content,
        CancellationToken ct = default);

    Task<Stream> OpenAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
    Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default);

    /// <summary>Removes every stored file belonging to a tenant. Called when a workspace is
    /// deleted, so offboarding a customer leaves nothing behind.</summary>
    Task DeleteTenantAsync(Guid tenantId, CancellationToken ct = default);
}

public static class StorageKey
{
    public static string Build(Guid tenantId, Guid knowledgeBaseId, string fileName)
    {
        var safeName = string.Concat(Path.GetFileName(fileName)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return $"{tenantId}/{knowledgeBaseId}/{Guid.NewGuid():N}_{safeName}";
    }

    /// <summary>First segment of the key. Used to place a blob in its tenant's own container when
    /// the per-tenant container strategy is active.</summary>
    public static (string tenant, string remainder) SplitTenant(string storageKey)
    {
        var slash = storageKey.IndexOf('/');
        return slash < 0 ? ("", storageKey) : (storageKey[..slash], storageKey[(slash + 1)..]);
    }
}

public class LocalFileStorage : IDocumentStorage
{
    private readonly string _root;

    public LocalFileStorage(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config["Storage:LocalRoot"];
        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "App_Data", "documents")
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(_root);
    }

    public string ProviderName => "LocalFileSystem";

    public string Root => _root;

    public async Task<string> SaveAsync(Guid tenantId, Guid knowledgeBaseId, string fileName,
        Stream content, CancellationToken ct = default)
    {
        var key = StorageKey.Build(tenantId, knowledgeBaseId, fileName);
        var full = ToPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Callers often hand over a buffer they have already read to the end (hashing, validation),
        // so rewind rather than silently writing zero bytes.
        if (content.CanSeek) content.Position = 0;
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);
        return key;
    }

    public Task<Stream> OpenAsync(string storageKey, CancellationToken ct = default)
        => Task.FromResult<Stream>(File.OpenRead(ToPath(storageKey)));

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var path = ToPath(storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default)
        => Task.FromResult(File.Exists(ToPath(storageKey)));

    public Task DeleteTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var directory = ToPath(tenantId.ToString());
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    public string ToPath(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Storage key escapes the storage root.");
        return full;
    }
}
