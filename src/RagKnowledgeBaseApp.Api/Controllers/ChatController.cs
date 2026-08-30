using System.Text;
using System.Text.Json;
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

namespace RagKnowledgeBaseApp.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _current;
    private readonly RagService _rag;
    private readonly AuditService _audit;
    private readonly IDocumentStorage _storage;
    private readonly IngestionQueue _queue;
    private readonly IVectorStore _vectors;

    public ChatController(AppDbContext db, CurrentUser current, RagService rag, AuditService audit,
        IDocumentStorage storage, IngestionQueue queue, IVectorStore vectors)
    {
        _db = db;
        _current = current;
        _rag = rag;
        _audit = audit;
        _storage = storage;
        _queue = queue;
        _vectors = vectors;
    }

    // ---------------- conversations ----------------

    [HttpGet("conversations")]
    public async Task<ActionResult<ConversationDto[]>> Conversations([FromQuery] string? search,
        CancellationToken ct)
    {
        var query = _db.Conversations.AsNoTracking().Include(c => c.Chatbot)
            .Where(c => c.TenantId == _current.TenantId && c.UserId == _current.Id && !c.IsArchived);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            // Match the title or anything said inside the conversation.
            query = query.Where(c => EF.Functions.Like(c.Title, $"%{term}%") ||
                _db.Messages.Any(m => m.ConversationId == c.Id && EF.Functions.Like(m.Content, $"%{term}%")));
        }

        var conversations = await query.OrderByDescending(c => c.UpdatedAt).Take(200).ToListAsync(ct);
        var ids = conversations.Select(c => c.Id).ToList();
        var counts = await _db.Messages.Where(m => ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return Ok(conversations.Select(c => new ConversationDto(c.Id, c.ChatbotId,
            c.Chatbot?.Name ?? "", c.Title, c.CreatedAt, c.UpdatedAt, counts.GetValueOrDefault(c.Id)))
            .ToArray());
    }

    [HttpPost("conversations")]
    public async Task<ActionResult<ConversationDto>> StartConversation(StartConversationRequest request,
        CancellationToken ct)
    {
        var bot = await _db.Chatbots.FirstOrDefaultAsync(
            c => c.Id == request.ChatbotId && c.TenantId == _current.TenantId && c.IsActive, ct);
        if (bot is null) return NotFound(new { message = "Chatbot not found." });

        var conversation = new Conversation
        {
            TenantId = _current.TenantId,
            UserId = _current.Id,
            ChatbotId = bot.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New conversation" : request.Title.Trim()
        };
        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(ct);

        return Ok(new ConversationDto(conversation.Id, bot.Id, bot.Name, conversation.Title,
            conversation.CreatedAt, conversation.UpdatedAt, 0));
    }

    [HttpGet("conversations/{id:guid}/messages")]
    public async Task<ActionResult<MessageDto[]>> Messages(Guid id, CancellationToken ct)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return NotFound();
        var messages = await _db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == id && m.Role != MessageRole.System)
            .OrderBy(m => m.CreatedAt).ToListAsync(ct);
        return Ok(messages.Select(MapMessage).ToArray());
    }

    [HttpPut("conversations/{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, RenameRequest request, CancellationToken ct)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return NotFound();
        conversation.Title = string.IsNullOrWhiteSpace(request.Title)
            ? conversation.Title : request.Title.Trim();
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("conversations/{id:guid}")]
    public async Task<IActionResult> DeleteConversation(Guid id, CancellationToken ct)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return NotFound();

        // Files attached to this conversation are private to it and go away with it.
        var kbs = await _db.KnowledgeBases.Where(k => k.ConversationId == id).ToListAsync(ct);
        foreach (var kb in kbs)
        {
            var docs = await _db.Documents.Where(d => d.KnowledgeBaseId == kb.Id).ToListAsync(ct);
            foreach (var doc in docs) await _storage.DeleteAsync(doc.StorageKey, ct);
            await _vectors.DeleteByKnowledgeBaseAsync(kb.Id, ct);
            _db.KnowledgeBases.Remove(kb);
        }
        _db.Conversations.Remove(conversation);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("conversation.delete", "Conversation", id.ToString(), ct: ct);
        return NoContent();
    }

    [HttpGet("conversations/{id:guid}/export")]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return NotFound();
        var messages = await _db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == id && m.Role != MessageRole.System)
            .OrderBy(m => m.CreatedAt).ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# {conversation.Title}");
        sb.AppendLine();
        sb.AppendLine($"Exported {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        foreach (var m in messages)
        {
            sb.AppendLine($"## {(m.Role == MessageRole.User ? "Question" : "Answer")} - {m.CreatedAt:u}");
            sb.AppendLine();
            sb.AppendLine(m.Content);
            sb.AppendLine();
            var citations = Deserialize(m.CitationsJson);
            if (citations.Length > 0)
            {
                sb.AppendLine("Sources:");
                foreach (var c in citations)
                    sb.AppendLine($"- [{c.Index}] {c.FileName}{(c.Locator is null ? "" : " - " + c.Locator)} ({c.KnowledgeBase})");
                sb.AppendLine();
            }
        }
        await _audit.LogAsync("conversation.export", "Conversation", id.ToString(), ct: ct);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/markdown", $"conversation-{id:N}.md");
    }

    // ---------------- messaging ----------------

    [HttpPost("conversations/{id:guid}/messages")]
    public async Task<ActionResult<ChatResponse>> Send(Guid id, SendMessageRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "Message cannot be empty." });

        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return NotFound();
        var bot = await _db.Chatbots.FirstOrDefaultAsync(
            c => c.Id == conversation.ChatbotId && c.TenantId == _current.TenantId, ct);
        if (bot is null || !bot.IsActive)
            return BadRequest(new { message = "This chatbot is no longer available." });

        var question = request.Message.Trim();
        var userMessage = new Message
        {
            TenantId = _current.TenantId,
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = question
        };
        _db.Messages.Add(userMessage);
        await _db.SaveChangesAsync(ct);

        var answer = await _rag.AnswerAsync(bot, conversation, question, _current,
            request.AttachmentDocumentIds ?? Array.Empty<Guid>(), ct);

        var assistantMessage = new Message
        {
            TenantId = _current.TenantId,
            ConversationId = conversation.Id,
            Role = MessageRole.Assistant,
            Content = answer.Content,
            CitationsJson = JsonSerializer.Serialize(answer.Citations),
            Model = answer.Model,
            PromptTokens = answer.PromptTokens,
            CompletionTokens = answer.CompletionTokens,
            LatencyMs = answer.LatencyMs,
            NoAnswer = answer.NoAnswer
        };
        _db.Messages.Add(assistantMessage);

        conversation.UpdatedAt = DateTime.UtcNow;
        if (conversation.Title == "New conversation")
            conversation.Title = question.Length <= 60 ? question : question[..60] + "...";
        if (!bot.KeepChatHistory)
        {
            // History disabled: keep only this exchange so the transcript never accumulates.
            var older = await _db.Messages.Where(m => m.ConversationId == conversation.Id
                && m.Id != userMessage.Id).ToListAsync(ct);
            _db.Messages.RemoveRange(older);
        }
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("chat.message", "Conversation", conversation.Id.ToString(), new
        {
            Chatbot = bot.Name,
            Question = question.Length <= 300 ? question : question[..300],
            Sources = answer.Citations.Select(c => c.FileName).Distinct().ToArray(),
            answer.Model,
            answer.PromptTokens,
            answer.CompletionTokens,
            answer.LatencyMs,
            answer.NoAnswer
        }, ct);

        return Ok(new ChatResponse(conversation.Id, MapMessage(assistantMessage), answer.FollowUpQuestions));
    }

    [HttpPost("conversations/{id:guid}/regenerate")]
    public async Task<ActionResult<ChatResponse>> Regenerate(Guid id, CancellationToken ct)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return NotFound();

        var lastUser = await _db.Messages.Where(m => m.ConversationId == id && m.Role == MessageRole.User)
            .OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct);
        if (lastUser is null) return BadRequest(new { message = "There is nothing to regenerate." });

        // Drop the previous answer so the transcript does not end up with two replies to one question.
        var staleAnswers = await _db.Messages.Where(m => m.ConversationId == id
            && m.Role == MessageRole.Assistant && m.CreatedAt > lastUser.CreatedAt).ToListAsync(ct);
        _db.Messages.RemoveRange(staleAnswers);
        _db.Messages.Remove(lastUser);
        await _db.SaveChangesAsync(ct);

        return await Send(id, new SendMessageRequest(lastUser.Content, null), ct);
    }

    [HttpPost("messages/{messageId:guid}/feedback")]
    public async Task<IActionResult> SubmitFeedback(Guid messageId, FeedbackRequest request,
        CancellationToken ct)
    {
        var message = await _db.Messages.Include(m => m.Conversation)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.TenantId == _current.TenantId, ct);
        if (message?.Conversation is null || message.Conversation.UserId != _current.Id) return NotFound();

        message.Feedback = Enum.TryParse<Feedback>(request.Feedback, true, out var f) ? f : Feedback.None;
        message.FeedbackComment = request.Comment;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("chat.feedback", "Message", messageId.ToString(),
            new { Feedback = message.Feedback.ToString(), request.Comment }, ct);
        return NoContent();
    }

    // ---------------- end-user attachments ----------------

    /// <summary>Files a user attaches while chatting. They land in a conversation-scoped knowledge
    /// base that only that user can retrieve from, and never join the company knowledge base.</summary>
    [HttpPost("conversations/{id:guid}/attachments")]
    [RequestSizeLimit(25L * 1024 * 1024)]
    public async Task<ActionResult<DocumentDto[]>> Attach(Guid id, [FromForm] IFormFileCollection files,
        CancellationToken ct)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return NotFound();
        var bot = await _db.Chatbots.FirstOrDefaultAsync(c => c.Id == conversation.ChatbotId, ct);
        if (bot is null || !bot.AllowUserUpload)
            return BadRequest(new { message = "This chatbot does not accept user uploads." });
        if (files is null || files.Count == 0)
            return BadRequest(new { message = "No files were uploaded." });

        var kb = await _db.KnowledgeBases.FirstOrDefaultAsync(
            k => k.ConversationId == id && k.OwnerUserId == _current.Id, ct);
        if (kb is null)
        {
            kb = new KnowledgeBase
            {
                TenantId = _current.TenantId,
                Name = $"Attachments: {conversation.Title}",
                Scope = KnowledgeBaseScope.Conversation,
                OwnerUserId = _current.Id,
                ConversationId = id,
                Description = "Temporary files attached to a single conversation."
            };
            _db.KnowledgeBases.Add(kb);
            await _db.SaveChangesAsync(ct);
        }

        var created = new List<Document>();
        foreach (var file in files)
        {
            if (file.Length == 0) continue;
            if (!SupportedFiles.IsSupported(file.FileName))
                return BadRequest(new { message = $"'{file.FileName}' is not a supported type." });

            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var sha = DocumentProcessor.ComputeSha256(buffer);
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
                Classification = Classification.Internal,
                IsEphemeral = true,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                UploadedByUserId = _current.Id
            };
            _db.Documents.Add(doc);
            created.Add(doc);
        }
        await _db.SaveChangesAsync(ct);
        foreach (var doc in created) await _queue.EnqueueAsync(doc.Id);
        await _audit.LogAsync("chat.attach", "Conversation", id.ToString(),
            new { Files = created.Select(d => d.FileName).ToArray() }, ct);

        return Ok(created.Select(d => new DocumentDto(d.Id, d.KnowledgeBaseId, kb.Name, d.FileName,
            d.ContentType, d.SizeBytes, d.Status.ToString(), null, 0, 1, d.Classification.ToString(),
            true, d.CreatedAt, null, _current.DisplayName)).ToArray());
    }

    [HttpGet("conversations/{id:guid}/attachments")]
    public async Task<ActionResult<DocumentDto[]>> Attachments(Guid id, CancellationToken ct)
    {
        var conversation = await LoadConversationAsync(id, ct);
        if (conversation is null) return NotFound();
        var docs = await _db.Documents.AsNoTracking().Include(d => d.KnowledgeBase)
            .Where(d => d.KnowledgeBase!.ConversationId == id && d.KnowledgeBase.OwnerUserId == _current.Id)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return Ok(docs.Select(d => new DocumentDto(d.Id, d.KnowledgeBaseId, d.KnowledgeBase?.Name ?? "",
            d.FileName, d.ContentType, d.SizeBytes, d.Status.ToString(), d.ErrorMessage, d.ChunkCount,
            d.Version, d.Classification.ToString(), d.IsEphemeral, d.CreatedAt, d.IndexedAt,
            _current.DisplayName)).ToArray());
    }

    private Task<Conversation?> LoadConversationAsync(Guid id, CancellationToken ct)
        => _db.Conversations.FirstOrDefaultAsync(
            c => c.Id == id && c.TenantId == _current.TenantId && c.UserId == _current.Id, ct);

    private static CitationDto[] Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<CitationDto>();
        try { return JsonSerializer.Deserialize<CitationDto[]>(json) ?? Array.Empty<CitationDto>(); }
        catch (JsonException) { return Array.Empty<CitationDto>(); }
    }

    private static MessageDto MapMessage(Message m) => new(m.Id, m.Role.ToString(), m.Content,
        Deserialize(m.CitationsJson), m.Model, m.PromptTokens, m.CompletionTokens, m.LatencyMs,
        m.NoAnswer, m.Feedback.ToString(), m.CreatedAt);
}
