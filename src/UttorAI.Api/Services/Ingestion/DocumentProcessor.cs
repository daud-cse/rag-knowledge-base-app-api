using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using UttorAI.Api.Data;
using UttorAI.Api.Domain;
using UttorAI.Api.Services.Llm;
using UttorAI.Api.Services.Storage;
using UttorAI.Api.Services.Vector;

namespace UttorAI.Api.Services.Ingestion;

/// <summary>Hand-off between the upload endpoint and the ingestion worker, so an upload returns as
/// soon as the bytes are stored and the caller can poll document status.</summary>
public class IngestionQueue
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(Guid documentId) => _channel.Writer.WriteAsync(documentId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public class IngestionWorker : BackgroundService
{
    private readonly IngestionQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(IngestionQueue queue, IServiceScopeFactory scopes, ILogger<IngestionWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var documentId in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<DocumentProcessor>();
                await processor.ProcessAsync(documentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion worker failed for document {DocumentId}", documentId);
            }
        }
    }
}

/// <summary>Upload -> validate -> extract -> chunk -> embed -> index, with the document status
/// advanced at each step so the admin UI can show where a file is (or why it failed).</summary>
public class DocumentProcessor
{
    private readonly AppDbContext _db;
    private readonly IDocumentStorage _storage;
    private readonly TextExtractionService _extraction;
    private readonly Chunker _chunker;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IVectorStore _vectors;
    private readonly ILogger<DocumentProcessor> _logger;

    public DocumentProcessor(AppDbContext db, IDocumentStorage storage, TextExtractionService extraction,
        Chunker chunker, IEmbeddingProvider embeddings, IVectorStore vectors,
        ILogger<DocumentProcessor> logger)
    {
        _db = db;
        _storage = storage;
        _extraction = extraction;
        _chunker = chunker;
        _embeddings = embeddings;
        _vectors = vectors;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.Documents.Include(d => d.KnowledgeBase)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null) return;

        try
        {
            await SetStatus(doc, DocumentStatus.Validating, ct);
            if (!SupportedFiles.IsSupported(doc.FileName))
                throw new NotSupportedException($"Unsupported file type '{Path.GetExtension(doc.FileName)}'.");
            if (!await _storage.ExistsAsync(doc.StorageKey, ct))
                throw new FileNotFoundException("Stored file is missing.");

            await SetStatus(doc, DocumentStatus.Extracting, ct);
            IReadOnlyList<ExtractedSegment> segments;
            await using (var stream = await _storage.OpenAsync(doc.StorageKey, ct))
            {
                segments = _extraction.Extract(stream, doc.FileName, doc.ContentType);
            }
            if (segments.Count == 0 || segments.All(s => string.IsNullOrWhiteSpace(s.Text)))
                throw new InvalidOperationException("No text could be extracted from this document.");

            await SetStatus(doc, DocumentStatus.Chunking, ct);
            var kb = doc.KnowledgeBase!;
            var chunks = _chunker.Chunk(segments, kb.ChunkSize, kb.ChunkOverlap);
            if (chunks.Count == 0) throw new InvalidOperationException("Chunking produced no content.");

            await SetStatus(doc, DocumentStatus.Embedding, ct);
            // Replace any previous entries for this document, in the database and in the index,
            // before writing new ones.
            await _db.DocumentChunks.Where(c => c.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
            await _vectors.DeleteByDocumentAsync(doc.Id, ct);

            const int batchSize = 32;
            var ordinal = 0;
            for (var i = 0; i < chunks.Count; i += batchSize)
            {
                var batch = chunks.Skip(i).Take(batchSize).ToList();
                var vectors = await _embeddings.EmbedBatchAsync(
                    batch.Select(b => b.Text).ToList(), kb.EmbeddingModel, ct);

                var entities = new List<DocumentChunk>(batch.Count);
                for (var j = 0; j < batch.Count; j++)
                {
                    entities.Add(new DocumentChunk
                    {
                        TenantId = doc.TenantId,
                        KnowledgeBaseId = doc.KnowledgeBaseId,
                        DocumentId = doc.Id,
                        Ordinal = ordinal++,
                        Text = batch[j].Text,
                        Locator = batch[j].Locator,
                        TokenEstimate = Tokenizer.EstimateTokens(batch[j].Text),
                        Classification = doc.Classification,
                        OwnerUserId = kb.Scope == KnowledgeBaseScope.Company ? null : kb.OwnerUserId,
                        Embedding = VectorMath.ToBytes(vectors[j])
                    });
                }

                // The chunk rows are the system of record; the vector store is the index over them.
                _db.DocumentChunks.AddRange(entities);
                await _db.SaveChangesAsync(ct);
                await _vectors.UpsertAsync(entities, ct);
            }

            doc.ChunkCount = ordinal;
            doc.IndexedAt = DateTime.UtcNow;
            doc.ErrorMessage = null;
            await SetStatus(doc, DocumentStatus.Indexed, ct);

            kb.LastIndexedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Indexed {File} ({Chunks} chunks) into knowledge base {Kb}",
                doc.FileName, ordinal, kb.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process document {DocumentId}", documentId);
            doc.ErrorMessage = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            doc.Status = DocumentStatus.Failed;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task SetStatus(Document doc, DocumentStatus status, CancellationToken ct)
    {
        doc.Status = status;
        await _db.SaveChangesAsync(ct);
    }

    public static string ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        var hash = SHA256.HashData(stream);
        stream.Position = 0;
        return Convert.ToHexString(hash);
    }
}
