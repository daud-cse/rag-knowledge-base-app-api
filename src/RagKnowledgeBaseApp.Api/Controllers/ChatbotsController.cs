using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services;

namespace RagKnowledgeBaseApp.Api.Controllers;

[ApiController]
[Route("api/chatbots")]
[Authorize]
public class ChatbotsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _current;
    private readonly AuditService _audit;

    public ChatbotsController(AppDbContext db, CurrentUser current, AuditService audit)
    {
        _db = db;
        _current = current;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ChatbotDto[]>> List([FromQuery] bool onlyActive, CancellationToken ct)
    {
        var query = _db.Chatbots.AsNoTracking()
            .Include(c => c.KnowledgeBases).ThenInclude(m => m.KnowledgeBase)
            .Where(c => c.TenantId == _current.TenantId);
        if (onlyActive) query = query.Where(c => c.IsActive);
        var bots = await query.OrderBy(c => c.Name).ToListAsync(ct);
        return Ok(bots.Select(Map).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ChatbotDto>> Get(Guid id, CancellationToken ct)
    {
        var bot = await LoadAsync(id, ct);
        return bot is null ? NotFound() : Ok(Map(bot));
    }

    [HttpPost]
    [Authorize(Policy = Policies.ChatbotAdmin)]
    public async Task<ActionResult<ChatbotDto>> Create(SaveChatbotRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        var bot = new Chatbot { TenantId = _current.TenantId, Name = request.Name.Trim() };
        Apply(bot, request);
        _db.Chatbots.Add(bot);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("chatbot.create", "Chatbot", bot.Id.ToString(), new { bot.Name }, ct);
        return Ok(Map(bot));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.ChatbotAdmin)]
    public async Task<ActionResult<ChatbotDto>> Update(Guid id, SaveChatbotRequest request,
        CancellationToken ct)
    {
        var bot = await LoadAsync(id, ct);
        if (bot is null) return NotFound();
        Apply(bot, request);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("chatbot.update", "Chatbot", id.ToString(), ct: ct);
        return Ok(Map(bot));
    }

    [HttpPut("{id:guid}/knowledge-bases")]
    [Authorize(Policy = Policies.ChatbotAdmin)]
    public async Task<ActionResult<ChatbotDto>> MapKnowledgeBases(Guid id,
        MapKnowledgeBasesRequest request, CancellationToken ct)
    {
        var bot = await LoadAsync(id, ct);
        if (bot is null) return NotFound();

        var requested = request.KnowledgeBases?.Select(k => k.KnowledgeBaseId).Distinct().ToList() ?? new();
        // Only company knowledge bases in the same tenant may be attached to a shared chatbot.
        var valid = await _db.KnowledgeBases.AsNoTracking()
            .Where(k => requested.Contains(k.Id) && k.TenantId == _current.TenantId
                        && k.Scope == KnowledgeBaseScope.Company)
            .Select(k => k.Id).ToListAsync(ct);

        var rejected = requested.Except(valid).ToList();
        if (rejected.Count > 0)
            return BadRequest(new { message = "One or more knowledge bases are not available to this chatbot." });

        await _db.ChatbotKnowledgeBases.Where(m => m.ChatbotId == id).ExecuteDeleteAsync(ct);
        foreach (var link in request.KnowledgeBases ?? Array.Empty<KnowledgeBaseLinkDto>())
        {
            _db.ChatbotKnowledgeBases.Add(new ChatbotKnowledgeBase
            {
                ChatbotId = id,
                KnowledgeBaseId = link.KnowledgeBaseId,
                Priority = link.Priority <= 0 ? 1 : link.Priority
            });
        }
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("chatbot.map-kb", "Chatbot", id.ToString(), new { Count = valid.Count }, ct);

        var reloaded = await LoadAsync(id, ct);
        return Ok(Map(reloaded!));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.ChatbotAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var bot = await LoadAsync(id, ct);
        if (bot is null) return NotFound();
        _db.Chatbots.Remove(bot);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("chatbot.delete", "Chatbot", id.ToString(), new { bot.Name }, ct);
        return NoContent();
    }

    private Task<Chatbot?> LoadAsync(Guid id, CancellationToken ct)
        => _db.Chatbots.Include(c => c.KnowledgeBases).ThenInclude(m => m.KnowledgeBase)
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == _current.TenantId, ct);

    private static void Apply(Chatbot bot, SaveChatbotRequest r)
    {
        if (!string.IsNullOrWhiteSpace(r.Name)) bot.Name = r.Name.Trim();
        if (r.Description is not null) bot.Description = r.Description;
        if (!string.IsNullOrWhiteSpace(r.SystemPrompt)) bot.SystemPrompt = r.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(r.Model)) bot.Model = r.Model;
        if (r.Temperature.HasValue) bot.Temperature = Math.Clamp(r.Temperature.Value, 0, 2);
        if (r.MaxTokens.HasValue) bot.MaxTokens = Math.Clamp(r.MaxTokens.Value, 64, 8000);
        if (r.RagEnabled.HasValue) bot.RagEnabled = r.RagEnabled.Value;
        if (r.CitationsEnabled.HasValue) bot.CitationsEnabled = r.CitationsEnabled.Value;
        if (r.TopK.HasValue) bot.TopK = Math.Clamp(r.TopK.Value, 1, 100);
        if (r.RerankTopN.HasValue) bot.RerankTopN = Math.Clamp(r.RerankTopN.Value, 1, 20);
        if (r.MaxContextTokens.HasValue)
            bot.MaxContextTokens = Math.Clamp(r.MaxContextTokens.Value, 1000, 100000);
        if (r.SimilarityThreshold.HasValue)
            bot.SimilarityThreshold = Math.Clamp(r.SimilarityThreshold.Value, 0, 1);
        if (r.HybridSearch.HasValue) bot.HybridSearch = r.HybridSearch.Value;
        if (r.QueryRewriting.HasValue) bot.QueryRewriting = r.QueryRewriting.Value;
        if (!string.IsNullOrWhiteSpace(r.ResponseLanguage)) bot.ResponseLanguage = r.ResponseLanguage;
        if (!string.IsNullOrWhiteSpace(r.WelcomeMessage)) bot.WelcomeMessage = r.WelcomeMessage;
        if (r.SuggestedQuestions is not null)
            bot.SuggestedQuestions = string.Join("\n", r.SuggestedQuestions.Where(q => !string.IsNullOrWhiteSpace(q)));
        if (r.AllowUserUpload.HasValue) bot.AllowUserUpload = r.AllowUserUpload.Value;
        if (r.ConversationTimeoutMinutes.HasValue)
            bot.ConversationTimeoutMinutes = Math.Clamp(r.ConversationTimeoutMinutes.Value, 5, 10080);
        if (r.KeepChatHistory.HasValue) bot.KeepChatHistory = r.KeepChatHistory.Value;
        if (r.IsActive.HasValue) bot.IsActive = r.IsActive.Value;
    }

    internal static ChatbotDto Map(Chatbot c) => new(
        c.Id, c.Name, c.Description, c.SystemPrompt, c.Model, c.Temperature, c.MaxTokens, c.RagEnabled,
        c.CitationsEnabled, c.TopK, c.RerankTopN, c.MaxContextTokens, c.SimilarityThreshold,
        c.HybridSearch, c.QueryRewriting,
        c.ResponseLanguage, c.WelcomeMessage,
        string.IsNullOrWhiteSpace(c.SuggestedQuestions)
            ? Array.Empty<string>()
            : c.SuggestedQuestions.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        c.AllowUserUpload, c.ConversationTimeoutMinutes, c.KeepChatHistory, c.IsActive, c.CreatedAt,
        c.KnowledgeBases.OrderBy(m => m.Priority)
            .Select(m => new KnowledgeBaseLinkDto(m.KnowledgeBaseId, m.KnowledgeBase?.Name ?? "", m.Priority))
            .ToArray());
}
