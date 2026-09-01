using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services.Llm;
using RagKnowledgeBaseApp.Api.Services.Storage;
using RagKnowledgeBaseApp.Api.Services.Vector;
using RagKnowledgeBaseApp.Api.Services.Quota;

namespace RagKnowledgeBaseApp.Api.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize(Policy = Policies.ChatbotAdmin)]
public class AnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _current;
    private readonly LlmOptions _llm;

    public AnalyticsController(AppDbContext db, CurrentUser current, LlmOptions llm)
    {
        _db = db;
        _current = current;
        _llm = llm;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<AnalyticsSummaryDto>> Summary([FromQuery] int days, CancellationToken ct)
    {
        var window = Math.Clamp(days == 0 ? 14 : days, 1, 90);
        var since = DateTime.UtcNow.Date.AddDays(-(window - 1));
        var tenant = _current.TenantId;

        var users = await _db.Users.CountAsync(u => u.TenantId == tenant, ct);
        var chatbots = await _db.Chatbots.CountAsync(c => c.TenantId == tenant, ct);
        var kbs = await _db.KnowledgeBases.CountAsync(
            k => k.TenantId == tenant && k.Scope == KnowledgeBaseScope.Company, ct);
        var documents = await _db.Documents.CountAsync(d => d.TenantId == tenant, ct);
        var failedDocuments = await _db.Documents.CountAsync(
            d => d.TenantId == tenant && d.Status == DocumentStatus.Failed, ct);
        var chunks = await _db.DocumentChunks.CountAsync(c => c.TenantId == tenant, ct);
        var conversations = await _db.Conversations.CountAsync(c => c.TenantId == tenant, ct);

        var answers = await _db.Messages.AsNoTracking()
            .Where(m => m.TenantId == tenant && m.Role == MessageRole.Assistant)
            .Select(m => new { m.NoAnswer, m.LatencyMs, m.PromptTokens, m.CompletionTokens, m.Feedback, m.CreatedAt })
            .ToListAsync(ct);

        var questions = answers.Count;
        var noAnswer = answers.Count(a => a.NoAnswer);
        var promptTokens = answers.Sum(a => (long)a.PromptTokens);
        var completionTokens = answers.Sum(a => (long)a.CompletionTokens);
        var cost = promptTokens / 1000d * _llm.PromptCostPer1K
                   + completionTokens / 1000d * _llm.CompletionCostPer1K;

        var perDay = Enumerable.Range(0, window)
            .Select(i => since.AddDays(i))
            .Select(day => new SeriesPointDto(day.ToString("MMM dd"),
                answers.Count(a => a.CreatedAt.Date == day)))
            .ToArray();

        // Grouped projections are materialised into anonymous types first: EF cannot translate a
        // positional record constructor inside a GroupBy projection.
        var topChatbots = (await _db.Conversations.AsNoTracking()
                .Where(c => c.TenantId == tenant)
                .GroupBy(c => c.Chatbot!.Name)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).Take(5).ToListAsync(ct))
            .Select(x => new NameCountDto(x.Name, x.Count)).ToArray();

        var topKbs = (await _db.Documents.AsNoTracking()
                .Where(d => d.TenantId == tenant && d.Status == DocumentStatus.Indexed)
                .GroupBy(d => d.KnowledgeBase!.Name)
                .Select(g => new { Name = g.Key, Count = g.Sum(d => d.ChunkCount) })
                .OrderByDescending(x => x.Count).Take(5).ToListAsync(ct))
            .Select(x => new NameCountDto(x.Name, x.Count)).ToArray();

        return Ok(new AnalyticsSummaryDto(
            users, chatbots, kbs, documents, chunks, conversations, questions,
            questions == 0 ? 0 : Math.Round(100.0 * (questions - noAnswer) / questions, 1),
            questions == 0 ? 0 : Math.Round(100.0 * noAnswer / questions, 1),
            questions == 0 ? 0 : Math.Round(answers.Average(a => a.LatencyMs) / 1000.0, 2),
            promptTokens, completionTokens, Math.Round(cost, 4),
            answers.Count(a => a.Feedback == Feedback.ThumbsUp),
            answers.Count(a => a.Feedback == Feedback.ThumbsDown),
            perDay, topChatbots, topKbs, failedDocuments));
    }

    [HttpGet("audit")]
    [Authorize(Policy = Policies.CompanyAdmin)]
    public async Task<ActionResult<PagedResult<AuditLogDto>>> Audit([FromQuery] int page,
        [FromQuery] int pageSize, [FromQuery] string? action, [FromQuery] Guid? tenantId,
        [FromQuery] string? email, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 10, 200);

        // A company admin is confined to their own tenant. A super admin runs the platform, so
        // they see every tenant by default and can narrow to one -- otherwise "who signed in"
        // could only ever be answered one company at a time.
        var platformWide = _current.Role == UserRole.SuperAdmin;

        var query = _db.AuditLogs.AsNoTracking();
        if (!platformWide) query = query.Where(a => a.TenantId == _current.TenantId);
        else if (tenantId is { } wanted) query = query.Where(a => a.TenantId == wanted);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => EF.Functions.Like(a.Action, $"%{action.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(email))
            query = query.Where(a => a.UserEmail != null
                                     && EF.Functions.Like(a.UserEmail, $"%{email.Trim()}%"));

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // Only the tenants on this page are looked up, so the cost does not grow with the log.
        var names = new Dictionary<Guid, string>();
        if (platformWide && items.Count > 0)
        {
            var ids = items.Select(a => a.TenantId).Distinct().ToList();
            names = await _db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        }

        return Ok(new PagedResult<AuditLogDto>(items.Select(a => new AuditLogDto(a.Id, a.UserEmail,
            a.Action, a.EntityType, a.EntityId, a.Details, a.IpAddress, a.Timestamp,
            platformWide ? names.GetValueOrDefault(a.TenantId) : null)).ToArray(),
            total, page, pageSize));
    }
}

[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly IChatCompletionProvider _chat;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IVectorStore _vectors;
    private readonly IDocumentStorage _storage;
    private readonly StorageHealth _storageHealth;
    private readonly IConfiguration _config;
    private readonly ITokenQuota _quota;
    private readonly CurrentUser _current;
    private readonly AppDbContext _db;

    public SystemController(IChatCompletionProvider chat, IEmbeddingProvider embeddings,
        IVectorStore vectors, IDocumentStorage storage, StorageHealth storageHealth,
        IConfiguration config, ITokenQuota quota, CurrentUser current, AppDbContext db)
    {
        _quota = quota;
        _current = current;
        _db = db;
        _chat = chat;
        _embeddings = embeddings;
        _vectors = vectors;
        _storage = storage;
        _storageHealth = storageHealth;
        _config = config;
    }

    /// <summary>What the signed-in user has spent today and what is left. The UI reads this to warn
    /// before an upload is refused rather than after.</summary>
    [HttpGet("quota")]
    [Authorize]
    public async Task<ActionResult<object>> Quota(CancellationToken ct)
    {
        var q = await _quota.GetAsync(_current.Id, _current.Role.ToString(), ct);
        return Ok(new
        {
            enabled = q.Enabled,
            used = q.Used,
            limit = q.Limit,
            remaining = q.Enabled ? q.Remaining : (long?)null,
            exceeded = q.Exceeded,
            resetsAtUtc = DateTime.UtcNow.Date.AddDays(1)
        });
    }

    /// <summary>Is the API able to serve a sign-in yet?
    ///
    /// This deliberately opens a database connection rather than just returning 200. The container
    /// scales to zero and Azure SQL auto-pauses independently, so the process can be answering
    /// requests a long time before the database has resumed — and a readiness check that only
    /// proved the container was up would hand the wait straight back to the login POST, which is
    /// the failure it exists to prevent.
    ///
    /// Requests to this endpoint are themselves what triggers both wake-ups, so polling it is the
    /// warm-up, not merely a test of it.</summary>
    [HttpGet("ready")]
    [AllowAnonymous]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        try
        {
            if (await _db.Database.CanConnectAsync(ct))
                return Ok(new { ready = true });
        }
        catch
        {
            // Still resuming. 503 keeps the client retrying instead of treating it as fatal.
        }
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { ready = false });
    }

    [HttpGet("status")]
    [AllowAnonymous]
    public ActionResult<ProviderStatusDto> Status()
    {
        var notice = _chat.IsLive
            ? null
            : "No LLM credentials configured. Answers are assembled by the built-in extractive engine " +
              "from the retrieved passages. Set Llm:Provider and Llm:ApiKey to use OpenAI or Azure OpenAI.";

        // A broken document store is more urgent than a missing LLM key, so it wins the banner.
        if (!_storageHealth.Healthy)
            notice = $"Document storage is unreachable: {_storageHealth.Error} " +
                     "Uploads will fail until this is fixed.";

        return Ok(new ProviderStatusDto(
            _chat.ProviderName, _embeddings.ProviderName, _vectors.ProviderName, _storage.ProviderName,
            _config["Database:Provider"] ?? "Sqlite", _chat.IsLive, notice));
    }

    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult Health() => Ok(new { status = "ok", utc = DateTime.UtcNow });
}
