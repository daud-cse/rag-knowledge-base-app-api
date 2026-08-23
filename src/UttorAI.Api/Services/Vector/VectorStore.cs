using Microsoft.EntityFrameworkCore;
using UttorAI.Api.Data;
using UttorAI.Api.Domain;
using UttorAI.Api.Services.Llm;

namespace UttorAI.Api.Services.Vector;

/// <summary>Security filter applied *before* any chunk is returned to the LLM. Section 10 of the
/// technical document calls this security trimming.</summary>
public record RetrievalFilter(
    Guid TenantId,
    IReadOnlyCollection<Guid> KnowledgeBaseIds,
    Guid UserId,
    Classification MaxClassification);

public record VectorHit(
    Guid ChunkId,
    Guid DocumentId,
    Guid KnowledgeBaseId,
    string Text,
    string? Locator,
    int Ordinal,
    double Score);

public interface IVectorStore
{
    string ProviderName { get; }

    /// <summary>Called once at startup so an external index can create its collection.</summary>
    Task EnsureReadyAsync(CancellationToken ct = default);

    Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default);
    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task DeleteByKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken ct = default);

    /// <summary>Removes every vector belonging to a tenant, for workspace deletion.</summary>
    Task DeleteByTenantAsync(Guid tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<VectorHit>> SearchAsync(RetrievalFilter filter, float[] queryVector,
        string queryText, int topK, double minScore, bool hybrid, CancellationToken ct = default);
}

/// <summary>Vector index backed by the same relational database as the rest of the configuration.
/// Chunk vectors live on the chunk row, so tenant isolation is enforced by the same WHERE clause
/// that protects every other query. For larger corpora register the Qdrant adapter instead: it
/// implements this interface and takes the identical RetrievalFilter as a payload filter.</summary>
public class SqlVectorStore : IVectorStore
{
    private readonly AppDbContext _db;

    public SqlVectorStore(AppDbContext db) => _db = db;

    public string ProviderName => "SQL-backed (in-process cosine)";

    public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default)
    {
        // The vectors live on the chunk rows DocumentProcessor already tracks, so persisting the
        // ingestion transaction is the whole of the upsert.
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        await _db.DocumentChunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteByKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken ct = default)
    {
        await _db.DocumentChunks.Where(c => c.KnowledgeBaseId == knowledgeBaseId).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        await _db.DocumentChunks.Where(c => c.TenantId == tenantId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(RetrievalFilter filter, float[] queryVector,
        string queryText, int topK, double minScore, bool hybrid, CancellationToken ct = default)
    {
        if (filter.KnowledgeBaseIds.Count == 0) return Array.Empty<VectorHit>();

        var kbIds = filter.KnowledgeBaseIds.ToArray();

        // Tenant, knowledge base, document status and classification are all applied in SQL, so
        // trimmed chunks never reach this process, let alone the model.
        var candidates = await (
            from chunk in _db.DocumentChunks.AsNoTracking()
            join doc in _db.Documents.AsNoTracking() on chunk.DocumentId equals doc.Id
            where chunk.TenantId == filter.TenantId
                  && kbIds.Contains(chunk.KnowledgeBaseId)
                  && doc.Status == DocumentStatus.Indexed
                  && (int)chunk.Classification <= (int)filter.MaxClassification
                  && (chunk.OwnerUserId == null || chunk.OwnerUserId == filter.UserId)
            select new
            {
                chunk.Id, chunk.DocumentId, chunk.KnowledgeBaseId, chunk.Text,
                chunk.Locator, chunk.Ordinal, chunk.Embedding
            }).ToListAsync(ct);

        if (candidates.Count == 0) return Array.Empty<VectorHit>();

        var queryTerms = Tokenizer.Tokenize(queryText).ToHashSet();
        var hits = new List<VectorHit>(candidates.Count);

        foreach (var c in candidates)
        {
            var dense = VectorMath.Cosine(queryVector, VectorMath.FromBytes(c.Embedding));

            // The relevance decision is the semantic one. Keyword overlap only reorders what
            // survives: blending it into the score as a weighted average deflated every result by
            // up to 35% whenever the question shared no vocabulary with the corpus, which pushed
            // whole documents below the threshold.
            if (dense < minScore) continue;

            var score = dense;
            if (hybrid && queryTerms.Count > 0)
            {
                var terms = Tokenizer.Tokenize(c.Text);
                var matched = terms.Count == 0 ? 0 : terms.Distinct().Count(queryTerms.Contains);
                score = dense + 0.35 * ((double)matched / queryTerms.Count);
            }
            hits.Add(new VectorHit(c.Id, c.DocumentId, c.KnowledgeBaseId, c.Text, c.Locator, c.Ordinal, score));
        }

        return hits.OrderByDescending(h => h.Score).Take(Math.Max(1, topK)).ToList();
    }
}
