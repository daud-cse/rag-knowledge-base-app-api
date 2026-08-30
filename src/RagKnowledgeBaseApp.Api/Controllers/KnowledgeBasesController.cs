using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services;
using RagKnowledgeBaseApp.Api.Services.Vector;
using RagKnowledgeBaseApp.Api.Services.Storage;

namespace RagKnowledgeBaseApp.Api.Controllers;

[ApiController]
[Route("api/knowledge-bases")]
[Authorize]
public class KnowledgeBasesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _current;
    private readonly AuditService _audit;
    private readonly IVectorStore _vectors;
    private readonly IDocumentStorage _storage;

    public KnowledgeBasesController(AppDbContext db, CurrentUser current, AuditService audit,
        IVectorStore vectors, IDocumentStorage storage)
    {
        _db = db;
        _current = current;
        _audit = audit;
        _vectors = vectors;
        _storage = storage;
    }

    [HttpGet]
    public async Task<ActionResult<KnowledgeBaseDto[]>> List([FromQuery] string? scope, CancellationToken ct)
    {
        var query = _db.KnowledgeBases.AsNoTracking().Where(k => k.TenantId == _current.TenantId);

        // Company knowledge bases are visible to everyone in the tenant; personal ones only to
        // their owner. Conversation-scoped holders are an implementation detail and stay hidden.
        query = query.Where(k => k.Scope == KnowledgeBaseScope.Company ||
                                 (k.Scope == KnowledgeBaseScope.Personal && k.OwnerUserId == _current.Id));

        if (!string.IsNullOrWhiteSpace(scope) && Enum.TryParse<KnowledgeBaseScope>(scope, true, out var s))
            query = query.Where(k => k.Scope == s);

        var kbs = await query.OrderBy(k => k.Scope).ThenBy(k => k.Name).ToListAsync(ct);
        var ids = kbs.Select(k => k.Id).ToList();

        var docCounts = await _db.Documents.Where(d => ids.Contains(d.KnowledgeBaseId))
            .GroupBy(d => d.KnowledgeBaseId)
            .Select(g => new { g.Key, Docs = g.Count(), Chunks = g.Sum(x => x.ChunkCount) })
            .ToListAsync(ct);

        return Ok(kbs.Select(k =>
        {
            var counts = docCounts.FirstOrDefault(c => c.Key == k.Id);
            return Map(k, counts?.Docs ?? 0, counts?.Chunks ?? 0);
        }).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseDto>> Get(Guid id, CancellationToken ct)
    {
        var kb = await LoadAsync(id, ct);
        if (kb is null) return NotFound();
        var docs = await _db.Documents.Where(d => d.KnowledgeBaseId == id)
            .Select(d => d.ChunkCount).ToListAsync(ct);
        return Ok(Map(kb, docs.Count, docs.Sum()));
    }

    [HttpPost]
    public async Task<ActionResult<KnowledgeBaseDto>> Create(CreateKnowledgeBaseRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        var scope = Enum.TryParse<KnowledgeBaseScope>(request.Scope, true, out var s)
            ? s : KnowledgeBaseScope.Company;
        if (scope == KnowledgeBaseScope.Conversation)
            return BadRequest(new { message = "Conversation knowledge bases are created automatically." });
        if (scope == KnowledgeBaseScope.Company && !_current.IsAtLeast(UserRole.KnowledgeAdmin))
            return Forbid();

        var kb = new KnowledgeBase
        {
            TenantId = _current.TenantId,
            Name = request.Name.Trim(),
            Description = request.Description,
            Scope = scope,
            OwnerUserId = scope == KnowledgeBaseScope.Personal ? _current.Id : null,
            ChunkSize = request.ChunkSize ?? 900,
            ChunkOverlap = request.ChunkOverlap ?? 150,
            EmbeddingModel = string.IsNullOrWhiteSpace(request.EmbeddingModel)
                ? "text-embedding-3-small" : request.EmbeddingModel
        };
        _db.KnowledgeBases.Add(kb);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("kb.create", "KnowledgeBase", kb.Id.ToString(),
            new { kb.Name, Scope = scope.ToString() }, ct);
        return Ok(Map(kb, 0, 0));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseDto>> Update(Guid id, UpdateKnowledgeBaseRequest request,
        CancellationToken ct)
    {
        var kb = await LoadAsync(id, ct);
        if (kb is null) return NotFound();
        if (!CanManage(kb)) return Forbid();

        if (!string.IsNullOrWhiteSpace(request.Name)) kb.Name = request.Name.Trim();
        if (request.Description is not null) kb.Description = request.Description;
        if (request.ChunkSize.HasValue) kb.ChunkSize = Math.Clamp(request.ChunkSize.Value, 200, 8000);
        if (request.ChunkOverlap.HasValue) kb.ChunkOverlap = Math.Clamp(request.ChunkOverlap.Value, 0, 2000);
        if (!string.IsNullOrWhiteSpace(request.EmbeddingModel)) kb.EmbeddingModel = request.EmbeddingModel;
        if (request.IsActive.HasValue) kb.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("kb.update", "KnowledgeBase", id.ToString(), ct: ct);

        var docs = await _db.Documents.Where(d => d.KnowledgeBaseId == id)
            .Select(d => d.ChunkCount).ToListAsync(ct);
        return Ok(Map(kb, docs.Count, docs.Sum()));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var kb = await LoadAsync(id, ct);
        if (kb is null) return NotFound();
        if (!CanManage(kb)) return Forbid();

        // Deleting the row cascades the documents and chunks, but the stored files and the vector
        // index sit outside the database and have to be cleaned up explicitly.
        var storageKeys = await _db.Documents.Where(d => d.KnowledgeBaseId == id)
            .Select(d => d.StorageKey).ToListAsync(ct);
        foreach (var key in storageKeys) await _storage.DeleteAsync(key, ct);
        await _vectors.DeleteByKnowledgeBaseAsync(id, ct);

        // Chatbot mappings are NO ACTION on this side (see AppDbContext), so unmap first.
        await _db.ChatbotKnowledgeBases.Where(m => m.KnowledgeBaseId == id).ExecuteDeleteAsync(ct);

        _db.KnowledgeBases.Remove(kb);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("kb.delete", "KnowledgeBase", id.ToString(),
            new { kb.Name, Files = storageKeys.Count }, ct);
        return NoContent();
    }

    private Task<KnowledgeBase?> LoadAsync(Guid id, CancellationToken ct)
        => _db.KnowledgeBases.FirstOrDefaultAsync(
            k => k.Id == id && k.TenantId == _current.TenantId &&
                 (k.Scope == KnowledgeBaseScope.Company || k.OwnerUserId == _current.Id), ct);

    private bool CanManage(KnowledgeBase kb) => kb.Scope == KnowledgeBaseScope.Company
        ? _current.IsAtLeast(UserRole.KnowledgeAdmin)
        : kb.OwnerUserId == _current.Id;

    private static KnowledgeBaseDto Map(KnowledgeBase k, int docs, int chunks) => new(
        k.Id, k.Name, k.Description, k.Scope.ToString(), k.OwnerUserId, k.ChunkSize, k.ChunkOverlap,
        k.EmbeddingModel, k.IsActive, k.CreatedAt, k.LastIndexedAt, docs, chunks);
}
