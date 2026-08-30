using System.Text;
using System.Text.Json;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Services.Llm;

namespace RagKnowledgeBaseApp.Api.Services.Vector;

public class VectorStoreOptions
{
    /// <summary>Sql or Qdrant.</summary>
    public string Provider { get; set; } = "Sql";
    public QdrantOptions Qdrant { get; set; } = new();
}

public class QdrantOptions
{
    public string Url { get; set; } = "http://localhost:6333";
    public string ApiKey { get; set; } = "";
    /// <summary>Base collection name. The vector width is appended, so switching embedding models
    /// lands in a separate collection instead of failing against an incompatible one.</summary>
    public string Collection { get; set; } = "ragkb_chunks";
}

/// <summary>Qdrant-backed implementation of the same contract as SqlVectorStore.
///
/// Tenant, knowledge base, classification and owner are pushed down as a Qdrant payload filter, so
/// security trimming happens inside the search engine and a trimmed point is never returned. The
/// document-status check stays in RagService, which resolves every hit against the database anyway.
/// </summary>
public class QdrantVectorStore : IVectorStore
{
    private readonly HttpClient _http;
    private readonly QdrantOptions _options;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(HttpClient http, VectorStoreOptions options, IEmbeddingProvider embeddings,
        ILogger<QdrantVectorStore> logger)
    {
        _options = options.Qdrant;
        _embeddings = embeddings;
        _logger = logger;
        _http = http;
        _http.BaseAddress = new Uri(_options.Url.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("api-key", _options.ApiKey);
    }

    private string Collection => $"{_options.Collection}_{_embeddings.Dimensions}";

    public string ProviderName => $"Qdrant ({_options.Url}, collection {Collection})";

    // ------------------------------- lifecycle -------------------------------

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        var existing = await _http.GetAsync($"collections/{Collection}", ct);
        if (existing.IsSuccessStatusCode)
        {
            _logger.LogInformation("Using existing Qdrant collection {Collection}", Collection);
            await EnsureIndexesAsync(ct);
            return;
        }

        await SendAsync(HttpMethod.Put, $"collections/{Collection}", new
        {
            vectors = new { size = _embeddings.Dimensions, distance = "Cosine" }
        }, ct);

        _logger.LogInformation("Created Qdrant collection {Collection} ({Dimensions} dimensions)",
            Collection, _embeddings.Dimensions);
        await EnsureIndexesAsync(ct);
    }

    /// <summary>Payload indexes on the fields every query filters by. Without these Qdrant still
    /// answers correctly but scans, which gets expensive as the corpus grows.</summary>
    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        foreach (var (field, schema) in new[]
        {
            ("tenantId", "keyword"), ("knowledgeBaseId", "keyword"),
            ("documentId", "keyword"), ("ownerUserId", "keyword"), ("classification", "integer")
        })
        {
            try
            {
                await SendAsync(HttpMethod.Put, $"collections/{Collection}/index?wait=true",
                    new { field_name = field, field_schema = schema }, ct);
            }
            catch (Exception ex)
            {
                // Already-exists comes back as an error; it is not worth failing startup over.
                _logger.LogDebug(ex, "Payload index {Field} not created", field);
            }
        }
    }

    // -------------------------------- writes ---------------------------------

    public async Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        var points = chunks.Select(c => new
        {
            id = c.Id.ToString(),
            vector = VectorMath.FromBytes(c.Embedding),
            payload = new
            {
                tenantId = c.TenantId.ToString(),
                knowledgeBaseId = c.KnowledgeBaseId.ToString(),
                documentId = c.DocumentId.ToString(),
                ownerUserId = c.OwnerUserId?.ToString(),
                classification = (int)c.Classification,
                ordinal = c.Ordinal,
                locator = c.Locator,
                text = c.Text
            }
        }).ToArray();

        await SendAsync(HttpMethod.Put, $"collections/{Collection}/points?wait=true",
            new { points }, ct);
    }

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
        => DeleteByFieldAsync("documentId", documentId, ct);

    public Task DeleteByKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken ct = default)
        => DeleteByFieldAsync("knowledgeBaseId", knowledgeBaseId, ct);

    public Task DeleteByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => DeleteByFieldAsync("tenantId", tenantId, ct);

    private Task DeleteByFieldAsync(string field, Guid value, CancellationToken ct)
        => SendAsync(HttpMethod.Post, $"collections/{Collection}/points/delete?wait=true", new
        {
            filter = new
            {
                must = new object[] { new { key = field, match = new { value = value.ToString() } } }
            }
        }, ct);

    // -------------------------------- search ---------------------------------

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(RetrievalFilter filter, float[] queryVector,
        string queryText, int topK, double minScore, bool hybrid, CancellationToken ct = default)
    {
        if (filter.KnowledgeBaseIds.Count == 0) return Array.Empty<VectorHit>();

        // Every clause here is security relevant and is evaluated by Qdrant, not by this process.
        var must = new List<object>
        {
            new { key = "tenantId", match = new { value = filter.TenantId.ToString() } },
            new
            {
                key = "knowledgeBaseId",
                match = new { any = filter.KnowledgeBaseIds.Select(id => id.ToString()).ToArray() }
            },
            new { key = "classification", range = new { lte = (int)filter.MaxClassification } }
        };

        // A chunk is visible if it belongs to no one in particular (company content) or to the
        // caller. Expressed as min_should so it is an explicit AND-ed requirement alongside must,
        // rather than relying on how a bare should interacts with must.
        var ownerClause = new
        {
            conditions = new object[]
            {
                new { is_null = new { key = "ownerUserId" } },
                new { key = "ownerUserId", match = new { value = filter.UserId.ToString() } }
            },
            min_count = 1
        };

        // Over-fetch when blending in keyword score, so lexical matches that rank just outside the
        // dense top-K can still surface.
        var limit = hybrid ? Math.Min(topK * 3, 200) : topK;

        using var doc = await SendAsync(HttpMethod.Post, $"collections/{Collection}/points/search", new
        {
            vector = queryVector,
            limit,
            with_payload = true,
            filter = new { must, min_should = ownerClause }
        }, ct);

        var queryTerms = Tokenizer.Tokenize(queryText).ToHashSet();
        var hits = new List<VectorHit>();

        foreach (var point in doc!.RootElement.GetProperty("result").EnumerateArray())
        {
            var payload = point.GetProperty("payload");
            var text = payload.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var dense = point.GetProperty("score").GetDouble();

            // The relevance decision is the semantic one. Keyword overlap only reorders what
            // survives, so a question whose words never appear in the corpus is not penalised.
            if (dense < minScore) continue;

            var score = dense;
            if (hybrid && queryTerms.Count > 0)
            {
                var terms = Tokenizer.Tokenize(text);
                var matched = terms.Count == 0 ? 0 : terms.Distinct().Count(queryTerms.Contains);
                score = dense + 0.35 * ((double)matched / queryTerms.Count);
            }

            hits.Add(new VectorHit(
                Guid.Parse(point.GetProperty("id").GetString()!),
                Guid.Parse(payload.GetProperty("documentId").GetString()!),
                Guid.Parse(payload.GetProperty("knowledgeBaseId").GetString()!),
                text,
                payload.TryGetProperty("locator", out var l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString() : null,
                payload.TryGetProperty("ordinal", out var o) ? o.GetInt32() : 0,
                score));
        }

        return hits.OrderByDescending(h => h.Score).Take(Math.Max(1, topK)).ToList();
    }

    // --------------------------------- http ----------------------------------

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Qdrant {method} {path} failed with {(int)response.StatusCode}: " +
                (content.Length <= 400 ? content : content[..400]));

        return string.IsNullOrWhiteSpace(content) ? null : JsonDocument.Parse(content);
    }
}
