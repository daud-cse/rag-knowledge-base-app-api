using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services;
using RagKnowledgeBaseApp.Api.Services.Vector;
using RagKnowledgeBaseApp.Api.Services.Ingestion;
using RagKnowledgeBaseApp.Api.Services.Storage;
using RagKnowledgeBaseApp.Api.Services.Quota;

namespace RagKnowledgeBaseApp.Api.Controllers;

[ApiController]
[Route("api/documents")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly CurrentUser _current;
    private readonly IDocumentStorage _storage;
    private readonly IngestionQueue _queue;
    private readonly AuditService _audit;
    private readonly IVectorStore _vectors;
    private readonly ITokenQuota _quota;

    public DocumentsController(AppDbContext db, CurrentUser current, IDocumentStorage storage,
        IngestionQueue queue, AuditService audit, IVectorStore vectors, ITokenQuota quota)
    {
        _quota = quota;
        _db = db;
        _current = current;
        _storage = storage;
        _queue = queue;
        _audit = audit;
        _vectors = vectors;
    }

    [HttpGet]
    public async Task<ActionResult<DocumentDto[]>> List([FromQuery] Guid? knowledgeBaseId,
        [FromQuery] string? status, CancellationToken ct)
    {
        var accessible = await AccessibleKnowledgeBaseIdsAsync(ct);
        var query = _db.Documents.AsNoTracking().Include(d => d.KnowledgeBase)
            .Where(d => d.TenantId == _current.TenantId && accessible.Contains(d.KnowledgeBaseId));

        if (knowledgeBaseId.HasValue) query = query.Where(d => d.KnowledgeBaseId == knowledgeBaseId.Value);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DocumentStatus>(status, true, out var s))
            query = query.Where(d => d.Status == s);

        // Security trimming applies to the document list too, not only to retrieval.
        query = query.Where(d => (int)d.Classification <= (int)_current.MaxClassification);

        var docs = await query.OrderByDescending(d => d.CreatedAt).Take(500).ToListAsync(ct);
        var uploaderIds = docs.Select(d => d.UploadedByUserId).Distinct().ToList();
        var uploaders = await _db.Users.AsNoTracking().Where(u => uploaderIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        return Ok(docs.Select(d => Map(d, uploaders.GetValueOrDefault(d.UploadedByUserId, "unknown"))).ToArray());
    }

    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult<DocumentDto[]>> Upload([FromForm] Guid knowledgeBaseId,
        [FromForm] string? classification, [FromForm] IFormFileCollection files, CancellationToken ct)
    {
        var kb = await _db.KnowledgeBases.FirstOrDefaultAsync(
            k => k.Id == knowledgeBaseId && k.TenantId == _current.TenantId, ct);
        if (kb is null) return NotFound(new { message = "Knowledge base not found." });

        // Uploading into the company knowledge base is an admin action; a normal user can only
        // upload into their own personal knowledge base.
        var isCompanyKb = kb.Scope == KnowledgeBaseScope.Company;
        if (isCompanyKb && !_current.IsAtLeast(UserRole.KnowledgeAdmin)) return Forbid();
        if (!isCompanyKb && kb.OwnerUserId != _current.Id) return Forbid();

        if (files is null || files.Count == 0)
            return BadRequest(new { message = "No files were uploaded." });

        var level = Enum.TryParse<Classification>(classification, true, out var c) ? c : Classification.Internal;
        if ((int)level > (int)_current.MaxClassification)
            return BadRequest(new { message = "You cannot classify a document above your own clearance." });

        // Indexing a document costs embedding tokens, so the daily allowance is checked before any
        // bytes are stored. The estimate is deliberately coarse -- roughly four characters per
        // token over the raw upload -- because the exact count is only known after extraction, and
        // refusing a clearly oversized upload up front beats storing a file we will not index.
        var quota = await _quota.GetAsync(_current.Id, _current.Role.ToString(), ct);
        if (quota.Enabled)
        {
            if (quota.Exceeded)
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    message = "Your token limit is exceeded for today. Uploads will work again "
                            + "after the daily reset at 00:00 UTC.",
                    used = quota.Used,
                    limit = quota.Limit,
                    remaining = 0L
                });

            var estimated = files.Sum(f => f.Length) / 4;
            if (estimated > quota.Remaining)
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    message = $"This upload needs about {estimated:N0} tokens to index but only "
                            + $"{quota.Remaining:N0} of your {quota.Limit:N0} daily tokens remain. "
                            + "Try a smaller file, or upload again after the reset at 00:00 UTC.",
                    used = quota.Used,
                    limit = quota.Limit,
                    remaining = quota.Remaining
                });
        }

        var created = new List<Document>();
        foreach (var file in files)
        {
            if (file.Length == 0) continue;
            if (file.Length > MaxUploadBytes)
                return BadRequest(new { message = $"'{file.FileName}' exceeds the 50 MB upload limit." });
            if (!SupportedFiles.IsSupported(file.FileName))
                return BadRequest(new
                {
                    message = $"'{file.FileName}' is not a supported type. " +
                              $"Supported: {string.Join(", ", SupportedFiles.Extensions)}"
                });

            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var sha = DocumentProcessor.ComputeSha256(buffer);

            // Re-uploading the same bytes creates a new version rather than a duplicate entry.
            var previous = await _db.Documents.Where(d => d.KnowledgeBaseId == kb.Id
                && d.FileName == file.FileName && d.Status != DocumentStatus.Archived)
                .OrderByDescending(d => d.Version).FirstOrDefaultAsync(ct);

            var key = await _storage.SaveAsync(_current.TenantId, kb.Id, file.FileName, buffer, ct);
            var doc = new Document
            {
                TenantId = _current.TenantId,
                KnowledgeBaseId = kb.Id,
                FileName = file.FileName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream" : file.ContentType,
                SizeBytes = file.Length,
                StorageKey = key,
                Sha256 = sha,
                Classification = kb.Scope == KnowledgeBaseScope.Company ? level : Classification.Internal,
                UploadedByUserId = _current.Id,
                Version = (previous?.Version ?? 0) + 1,
                SupersedesDocumentId = previous?.Id,
                Status = DocumentStatus.Uploaded
            };
            if (previous is not null) previous.Status = DocumentStatus.Archived;

            _db.Documents.Add(doc);
            created.Add(doc);
        }

        await _db.SaveChangesAsync(ct);

        // Archived versions must stop being retrievable immediately, not at next re-index.
        var supersededIds = created.Where(d => d.SupersedesDocumentId.HasValue)
            .Select(d => d.SupersedesDocumentId!.Value).ToList();
        if (supersededIds.Count > 0)
        {
            await _db.DocumentChunks.Where(c => supersededIds.Contains(c.DocumentId)).ExecuteDeleteAsync(ct);
            foreach (var superseded in supersededIds)
                await _vectors.DeleteByDocumentAsync(superseded, ct);
        }

        foreach (var doc in created) await _queue.EnqueueAsync(doc.Id);
        await _audit.LogAsync("document.upload", "KnowledgeBase", kb.Id.ToString(),
            new { Files = created.Select(d => d.FileName).ToArray(), Classification = level.ToString() }, ct);

        return Ok(created.Select(d => Map(d, _current.DisplayName, kb.Name)).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DocumentDto>> Get(Guid id, CancellationToken ct)
    {
        var doc = await LoadAsync(id, ct);
        if (doc is null) return NotFound();
        return Ok(Map(doc, "", doc.KnowledgeBase?.Name));
    }

    [HttpGet("{id:guid}/chunks")]
    public async Task<ActionResult<ChunkDto[]>> Chunks(Guid id, CancellationToken ct)
    {
        var doc = await LoadAsync(id, ct);
        if (doc is null) return NotFound();
        var chunks = await _db.DocumentChunks.AsNoTracking()
            .Where(c => c.DocumentId == id).OrderBy(c => c.Ordinal).Take(200).ToListAsync(ct);
        return Ok(chunks.Select(c => new ChunkDto(c.Id, c.Ordinal, c.Locator, c.TokenEstimate,
            c.Text.Length <= 600 ? c.Text : c.Text[..600] + "...")).ToArray());
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var doc = await LoadAsync(id, ct);
        if (doc is null) return NotFound();
        if (!await _storage.ExistsAsync(doc.StorageKey, ct))
            return NotFound(new { message = "Stored file is missing." });
        var stream = await _storage.OpenAsync(doc.StorageKey, ct);
        await _audit.LogAsync("document.download", "Document", id.ToString(), new { doc.FileName }, ct);
        return File(stream, doc.ContentType, doc.FileName);
    }

    [HttpPost("{id:guid}/reprocess")]
    public async Task<IActionResult> Reprocess(Guid id, CancellationToken ct)
    {
        var doc = await LoadAsync(id, ct);
        if (doc is null) return NotFound();
        if (!CanManage(doc)) return Forbid();

        doc.Status = DocumentStatus.Uploaded;
        doc.ErrorMessage = null;
        await _db.SaveChangesAsync(ct);
        await _queue.EnqueueAsync(doc.Id);
        await _audit.LogAsync("document.reprocess", "Document", id.ToString(), new { doc.FileName }, ct);
        return Accepted(new { doc.Id, Status = doc.Status.ToString() });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var doc = await LoadAsync(id, ct);
        if (doc is null) return NotFound();
        if (!CanManage(doc)) return Forbid();

        await _storage.DeleteAsync(doc.StorageKey, ct);
        await _vectors.DeleteByDocumentAsync(doc.Id, ct);
        _db.Documents.Remove(doc);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("document.delete", "Document", id.ToString(), new { doc.FileName }, ct);
        return NoContent();
    }

    private async Task<Document?> LoadAsync(Guid id, CancellationToken ct)
    {
        var accessible = await AccessibleKnowledgeBaseIdsAsync(ct);
        return await _db.Documents.Include(d => d.KnowledgeBase)
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == _current.TenantId
                && accessible.Contains(d.KnowledgeBaseId)
                && (int)d.Classification <= (int)_current.MaxClassification, ct);
    }

    private bool CanManage(Document doc)
        => doc.KnowledgeBase?.Scope == KnowledgeBaseScope.Company
            ? _current.IsAtLeast(UserRole.KnowledgeAdmin)
            : doc.KnowledgeBase?.OwnerUserId == _current.Id;

    private Task<List<Guid>> AccessibleKnowledgeBaseIdsAsync(CancellationToken ct)
        => _db.KnowledgeBases.AsNoTracking()
            .Where(k => k.TenantId == _current.TenantId &&
                        (k.Scope == KnowledgeBaseScope.Company || k.OwnerUserId == _current.Id))
            .Select(k => k.Id).ToListAsync(ct);

    private static DocumentDto Map(Document d, string uploader, string? kbName = null) => new(
        d.Id, d.KnowledgeBaseId, kbName ?? d.KnowledgeBase?.Name ?? "", d.FileName, d.ContentType,
        d.SizeBytes, d.Status.ToString(), d.ErrorMessage, d.ChunkCount, d.Version,
        d.Classification.ToString(), d.IsEphemeral, d.CreatedAt, d.IndexedAt, uploader);
}
