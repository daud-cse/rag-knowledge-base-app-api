using Microsoft.EntityFrameworkCore;
using UttorAI.Api.Data;

namespace UttorAI.Api.Services.Storage;

/// <summary>Copies documents that were stored on local disk into the active storage provider.
///
/// Switching an existing deployment from local files to Azure Blob would otherwise leave every
/// stored row pointing at bytes the new provider cannot see. Because both providers use the same
/// key shape, the copy is a straight read-then-write with no row updates at all. It runs once at
/// startup, skips anything already present, and never deletes the local copy.</summary>
public class StorageMigrator
{
    private readonly AppDbContext _db;
    private readonly IDocumentStorage _storage;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<StorageMigrator> _logger;

    public StorageMigrator(AppDbContext db, IDocumentStorage storage, IConfiguration config,
        IWebHostEnvironment env, ILogger<StorageMigrator> logger)
    {
        _db = db;
        _storage = storage;
        _config = config;
        _env = env;
        _logger = logger;
    }

    public async Task MigrateLocalFilesAsync(CancellationToken ct = default)
    {
        if (_storage is LocalFileStorage) return;

        var local = new LocalFileStorage(_config, _env);
        if (!Directory.Exists(local.Root)) return;

        var documents = await _db.Documents.AsNoTracking()
            .Select(d => new { d.Id, d.FileName, d.StorageKey }).ToListAsync(ct);

        var copied = 0;
        var missing = 0;

        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.StorageKey)) continue;
            if (await _storage.ExistsAsync(doc.StorageKey, ct)) continue;

            if (!await local.ExistsAsync(doc.StorageKey, ct))
            {
                missing++;
                continue;
            }

            try
            {
                await using var source = await local.OpenAsync(doc.StorageKey, ct);
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, ct);
                buffer.Position = 0;

                // Preserve the existing key rather than minting a new one, so no row has to change.
                await CopyPreservingKeyAsync(doc.StorageKey, buffer, ct);
                copied++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not copy {File} to {Provider}",
                    doc.FileName, _storage.ProviderName);
            }
        }

        if (copied > 0 || missing > 0)
            _logger.LogInformation(
                "Storage migration: copied {Copied} file(s) to {Provider}, {Missing} had no local copy.",
                copied, _storage.ProviderName, missing);
    }

    /// <summary>SaveAsync generates a fresh key by design. Migration needs the original key kept,
    /// so it writes through the provider's own primitives instead.</summary>
    private async Task CopyPreservingKeyAsync(string storageKey, Stream content, CancellationToken ct)
    {
        if (_storage is AzureBlobStorage blob)
        {
            await blob.UploadAtKeyAsync(storageKey, content, ct);
            return;
        }
        throw new NotSupportedException(
            $"Migration to {_storage.ProviderName} is not implemented.");
    }
}
