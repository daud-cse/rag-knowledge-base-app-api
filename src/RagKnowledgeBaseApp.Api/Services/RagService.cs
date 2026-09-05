using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services.Llm;
using RagKnowledgeBaseApp.Api.Services.Tools;
using RagKnowledgeBaseApp.Api.Services.Vector;

namespace RagKnowledgeBaseApp.Api.Services;

public record RagAnswer(
    string Content,
    List<CitationDto> Citations,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    int LatencyMs,
    bool NoAnswer,
    string[] FollowUpQuestions,
    /// <summary>Tool calls the model made while answering, and any it could not make without a
    /// person's approval. Empty for a chatbot with no tools attached.</summary>
    List<ToolCallSummary> ToolCalls);

/// <summary>What one tool call did, in the form the UI shows it.</summary>
public record ToolCallSummary(string Tool, string Operation, string Status, string? Error,
    Guid? InvocationId);

/// <summary>Query rewriting -> hybrid search (security trimmed) -> rerank -> context build ->
/// LLM -> citations, i.e. the pipeline in section 13 of the technical document.</summary>
public class RagService
{
    /// <summary>Used when a chatbot has no usable budget configured.</summary>
    private const int DefaultContextTokens = 12000;

    private readonly AppDbContext _db;
    private readonly IVectorStore _vectors;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IChatCompletionProvider _chat;
    private readonly ToolService _tools;
    private readonly ILogger<RagService> _logger;

    public RagService(AppDbContext db, IVectorStore vectors, IEmbeddingProvider embeddings,
        IChatCompletionProvider chat, ToolService tools, ILogger<RagService> logger)
    {
        _db = db;
        _vectors = vectors;
        _embeddings = embeddings;
        _chat = chat;
        _tools = tools;
        _logger = logger;
    }

    public async Task<RagAnswer> AnswerAsync(Chatbot bot, Conversation conversation, string question,
        CurrentUser user, IReadOnlyCollection<Guid> attachmentDocumentIds, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var history = await _db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Role != MessageRole.System)
            .OrderByDescending(m => m.CreatedAt).Take(10)
            .ToListAsync(ct);
        history.Reverse();

        var citations = new List<CitationDto>();
        var context = "";
        var hits = new List<(VectorHit hit, Document doc, KnowledgeBase kb)>();

        if (bot.RagEnabled)
        {
            var searchText = bot.QueryRewriting ? RewriteQuery(question, history) : question;
            var kbIds = await ResolveKnowledgeBaseIdsAsync(bot, conversation, user, ct);

            // A misconfigured or un-backfilled budget must never silently mean "send nothing".
            var contextBudget = bot.MaxContextTokens > 0 ? bot.MaxContextTokens : DefaultContextTokens;

            if (kbIds.Count > 0)
            {
                var filter = new RetrievalFilter(user.TenantId, kbIds, user.Id, user.MaxClassification);

                // Questions like "how many companies" or "list every policy" are aggregations: they
                // need every relevant passage, not the few most similar ones. Similarity search
                // cannot satisfy them by construction, so when the whole corpus fits in the context
                // budget it is sent in document order instead.
                var everything = await TryLoadWholeCorpusAsync(filter, contextBudget, ct);

                if (everything is not null)
                {
                    hits = everything;
                    _logger.LogInformation(
                        "Corpus fits the context budget: sending all {Count} chunks in document order.",
                        hits.Count);
                }
                else
                {
                    var wide = IsAggregationQuestion(question);
                    var topK = wide ? Math.Min(bot.TopK * 3, 100) : bot.TopK;

                    var queryVector = await _embeddings.EmbedAsync(searchText, "", ct);
                    var raw = await _vectors.SearchAsync(filter, queryVector, searchText,
                        Math.Max(topK, bot.RerankTopN), bot.SimilarityThreshold, bot.HybridSearch, ct);

                    hits = await HydrateAsync(raw, ct);
                    hits = Rerank(hits, searchText, attachmentDocumentIds, bot.RerankTopN,
                        contextBudget);

                    // Passages read better, and are easier for the model to reason over, in the
                    // order they appear in the source rather than by similarity score.
                    hits = hits.OrderBy(h => h.doc.FileName).ThenBy(h => h.hit.Ordinal).ToList();
                }

                (context, citations) = BuildContext(hits);
            }
        }

        var attachedTools = await _tools.ForChatbotAsync(bot.Id, user.TenantId, ct);
        var definitions = ToolService.Describe(attachedTools);

        var request = new ChatCompletionRequest(
            Model: bot.Model,
            SystemPrompt: ComposeSystemPrompt(bot),
            History: history.Select(m => new ChatTurn(
                m.Role == MessageRole.User ? "user" : "assistant", m.Content)).ToList(),
            UserMessage: question,
            Temperature: bot.Temperature,
            MaxTokens: bot.MaxTokens,
            Context: context,
            Tools: definitions.Count > 0 ? definitions : null);

        var result = await _chat.CompleteAsync(request, ct);

        // The model may ask for tools instead of answering. Run what it is allowed to run, feed the
        // results back and ask again. Tokens accumulate across rounds so the usage figures and the
        // quota reflect the whole exchange rather than only the final call.
        var toolSummaries = new List<ToolCallSummary>();
        var promptTokens = result.PromptTokens;
        var completionTokens = result.CompletionTokens;
        var completed = new List<(ToolCall, ToolResult)>();

        for (var round = 0; round < ToolService.MaxRounds && result.ToolCalls is { Count: > 0 }; round++)
        {
            foreach (var call in result.ToolCalls)
            {
                var resolved = _tools.Resolve(attachedTools, call.Name);

                if (resolved.Decision == ToolDecision.Unknown || resolved.Tool is null ||
                    resolved.Operation is null)
                {
                    // Telling the model plainly beats silence: it can then answer without the tool
                    // rather than waiting for a result that will never arrive.
                    completed.Add((call, new ToolResult(call.Id, call.Name,
                        "That tool is not available to this assistant.")));
                    toolSummaries.Add(new ToolCallSummary(call.Name, "", "unavailable",
                        "Not attached to this chatbot.", null));
                    continue;
                }

                if (resolved.Decision == ToolDecision.NeedsApproval)
                {
                    var pending = await _tools.RequestApprovalAsync(resolved.Tool, resolved.Operation,
                        call.ArgumentsJson, user.TenantId, user.Id, conversation.Id, ct);
                    completed.Add((call, new ToolResult(call.Id, call.Name,
                        "This action needs a person to approve it. It has been queued for approval; "
                        + "tell the user it is waiting and do not claim it has been done.")));
                    toolSummaries.Add(new ToolCallSummary(resolved.Tool.Name, resolved.Operation.Name,
                        "awaiting approval", null, pending.Id));
                    continue;
                }

                var execution = await _tools.ExecuteAsync(resolved.Tool, resolved.Operation,
                    call.ArgumentsJson, user.TenantId, user.Id, conversation.Id, ct);
                completed.Add((call, new ToolResult(call.Id, call.Name,
                    execution.Success ? execution.Content : $"The tool failed: {execution.Error}")));
                toolSummaries.Add(new ToolCallSummary(resolved.Tool.Name, resolved.Operation.Name,
                    execution.Success ? "ran" : "failed", execution.Error, null));
            }

            result = await _chat.CompleteAsync(request with { CompletedCalls = completed }, ct);
            promptTokens += result.PromptTokens;
            completionTokens += result.CompletionTokens;
        }

        sw.Stop();

        var used = bot.CitationsEnabled ? FilterToReferenced(result.Content, citations) : new List<CitationDto>();
        var followUps = SuggestFollowUps(hits, question);

        return new RagAnswer(result.Content, used, result.Model, promptTokens,
            completionTokens, (int)sw.ElapsedMilliseconds, result.NoAnswer, followUps, toolSummaries);
    }

    /// <summary>Company knowledge bases mapped to the chatbot, plus the caller's own personal
    /// knowledge bases and any files attached to this conversation. Every id returned belongs to
    /// the caller's tenant.</summary>
    private async Task<List<Guid>> ResolveKnowledgeBaseIdsAsync(Chatbot bot, Conversation conversation,
        CurrentUser user, CancellationToken ct)
    {
        var mapped = await _db.ChatbotKnowledgeBases.AsNoTracking()
            .Where(m => m.ChatbotId == bot.Id)
            .OrderBy(m => m.Priority)
            .Join(_db.KnowledgeBases.AsNoTracking().Where(k => k.IsActive && k.TenantId == user.TenantId),
                m => m.KnowledgeBaseId, k => k.Id, (m, k) => k.Id)
            .ToListAsync(ct);

        var personal = await _db.KnowledgeBases.AsNoTracking()
            .Where(k => k.TenantId == user.TenantId && k.IsActive
                        && k.Scope == KnowledgeBaseScope.Personal && k.OwnerUserId == user.Id)
            .Select(k => k.Id).ToListAsync(ct);

        var conversational = await _db.KnowledgeBases.AsNoTracking()
            .Where(k => k.TenantId == user.TenantId && k.Scope == KnowledgeBaseScope.Conversation
                        && k.ConversationId == conversation.Id && k.OwnerUserId == user.Id)
            .Select(k => k.Id).ToListAsync(ct);

        return mapped.Concat(personal).Concat(conversational).Distinct().ToList();
    }

    /// <summary>Returns every chunk the caller is allowed to see, in document order, when the whole
    /// set fits inside the context budget. Returns null when it does not, so the caller falls back
    /// to similarity search.
    ///
    /// The security predicates here are deliberately identical to the ones in the vector stores:
    /// tenant, knowledge base, document status, classification and chunk owner.</summary>
    private async Task<List<(VectorHit hit, Document doc, KnowledgeBase kb)>?> TryLoadWholeCorpusAsync(
        RetrievalFilter filter, int maxContextTokens, CancellationToken ct)
    {
        var kbIds = filter.KnowledgeBaseIds.ToArray();

        var trimmed = _db.DocumentChunks.AsNoTracking()
            .Join(_db.Documents.AsNoTracking(), c => c.DocumentId, d => d.Id, (c, d) => new { c, d })
            .Where(x => x.c.TenantId == filter.TenantId
                        && kbIds.Contains(x.c.KnowledgeBaseId)
                        && x.d.Status == DocumentStatus.Indexed
                        && (int)x.c.Classification <= (int)filter.MaxClassification
                        && (x.c.OwnerUserId == null || x.c.OwnerUserId == filter.UserId));

        // Cheap gate first: a large corpus should not be pulled into memory just to measure it.
        var totalTokens = await trimmed.SumAsync(x => (int?)x.c.TokenEstimate, ct) ?? 0;
        if (totalTokens == 0 || totalTokens > maxContextTokens) return null;

        var rows = await trimmed
            .OrderBy(x => x.d.FileName).ThenBy(x => x.c.Ordinal)
            .Select(x => new
            {
                x.c.Id, x.c.DocumentId, x.c.KnowledgeBaseId, x.c.Text, x.c.Locator, x.c.Ordinal
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return null;

        var docs = await _db.Documents.AsNoTracking()
            .Where(d => rows.Select(r => r.DocumentId).Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var kbs = await _db.KnowledgeBases.AsNoTracking()
            .Where(k => kbIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, ct);

        return rows
            .Where(r => docs.ContainsKey(r.DocumentId) && kbs.ContainsKey(r.KnowledgeBaseId))
            .Select(r => (
                new VectorHit(r.Id, r.DocumentId, r.KnowledgeBaseId, r.Text, r.Locator, r.Ordinal, 1.0),
                docs[r.DocumentId],
                kbs[r.KnowledgeBaseId]))
            .ToList();
    }

    /// <summary>Recognises questions that need breadth rather than the closest match: counting,
    /// listing, totalling or summarising across a whole corpus.</summary>
    private static bool IsAggregationQuestion(string question) => Regex.IsMatch(question,
        @"\b(how many|how much|count|total|number of|list (all|every|the)|all of the|every |
           each of|overall|altogether|summar(y|ise|ize)|compare|breakdown)\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);

    private async Task<List<(VectorHit, Document, KnowledgeBase)>> HydrateAsync(
        IReadOnlyList<VectorHit> hits, CancellationToken ct)
    {
        if (hits.Count == 0) return new();
        var docIds = hits.Select(h => h.DocumentId).Distinct().ToList();
        var kbIds = hits.Select(h => h.KnowledgeBaseId).Distinct().ToList();

        // Only live documents may be cited. An external index can lag a status change, so the
        // check lives here rather than in the store, and drops anything archived or reprocessing.
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => docIds.Contains(d.Id) && d.Status == DocumentStatus.Indexed)
            .ToDictionaryAsync(d => d.Id, ct);
        var kbs = await _db.KnowledgeBases.AsNoTracking().Where(k => kbIds.Contains(k.Id))
            .ToDictionaryAsync(k => k.Id, ct);

        return hits.Where(h => docs.ContainsKey(h.DocumentId) && kbs.ContainsKey(h.KnowledgeBaseId))
            .Select(h => (h, docs[h.DocumentId], kbs[h.KnowledgeBaseId]))
            .ToList();
    }

    /// <summary>Second-stage ranking over the candidate set: lexical coverage of the query, a small
    /// bonus for chunks the user just attached, and a penalty for near-duplicate passages.</summary>
    private static List<(VectorHit hit, Document doc, KnowledgeBase kb)> Rerank(
        List<(VectorHit hit, Document doc, KnowledgeBase kb)> candidates, string query,
        IReadOnlyCollection<Guid> attachmentDocumentIds, int topN, int maxContextTokens)
    {
        var queryTerms = Tokenizer.Tokenize(query).ToHashSet();
        var scored = candidates.Select(c =>
        {
            var terms = Tokenizer.Tokenize(c.hit.Text);
            var coverage = queryTerms.Count == 0 || terms.Count == 0
                ? 0
                : (double)queryTerms.Count(terms.Contains) / queryTerms.Count;
            var attachmentBoost = attachmentDocumentIds.Contains(c.doc.Id) ? 0.25 : 0;
            var freshness = c.doc.IndexedAt is null ? 0 : 0.02;
            return (c, score: 0.6 * c.hit.Score + 0.4 * coverage + attachmentBoost + freshness);
        }).OrderByDescending(x => x.score).ToList();

        var picked = new List<(VectorHit, Document, KnowledgeBase)>();
        var seen = new List<HashSet<string>>();
        var usedTokens = 0;

        foreach (var (candidate, _) in scored)
        {
            var terms = Tokenizer.Tokenize(candidate.hit.Text).ToHashSet();
            if (seen.Any(s => Jaccard(s, terms) > 0.85)) continue;

            // RerankTopN is a floor, not a ceiling: keep taking passages while the token budget
            // allows. Stopping at a handful of chunks is what makes broad questions unanswerable
            // even when the answer is sitting in the knowledge base.
            var cost = Tokenizer.EstimateTokens(candidate.hit.Text);
            if (picked.Count >= Math.Max(1, topN) && usedTokens + cost > maxContextTokens) break;

            seen.Add(terms);
            picked.Add(candidate);
            usedTokens += cost;
        }
        return picked;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersect = a.Count(b.Contains);
        return (double)intersect / (a.Count + b.Count - intersect);
    }

    private static (string context, List<CitationDto> citations) BuildContext(
        List<(VectorHit hit, Document doc, KnowledgeBase kb)> hits)
    {
        var sb = new StringBuilder();
        var citations = new List<CitationDto>();
        var index = 1;
        foreach (var (hit, doc, kb) in hits)
        {
            sb.AppendLine($"[{index}] {doc.FileName}{(hit.Locator is null ? "" : " - " + hit.Locator)}");
            sb.AppendLine(hit.Text);
            sb.AppendLine();
            citations.Add(new CitationDto(index, doc.Id, doc.FileName, hit.Locator, kb.Name,
                Math.Round(hit.Score, 4), Snippet(hit.Text)));
            index++;
        }
        return (sb.ToString().Trim(), citations);
    }

    private static string Snippet(string text)
    {
        var clean = Regex.Replace(text, @"\s+", " ").Trim();
        return clean.Length <= 320 ? clean : clean[..320] + "...";
    }

    private static string ComposeSystemPrompt(Chatbot bot)
    {
        var sb = new StringBuilder(bot.SystemPrompt.Trim());
        if (!bot.ResponseLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.Append($"Always reply in {bot.ResponseLanguage}.");
        }
        if (!bot.CitationsEnabled)
        {
            sb.AppendLine();
            sb.Append("Do not include citation markers in your reply.");
        }
        return sb.ToString();
    }

    /// <summary>Follow-up questions asked in the same conversation are often elliptical ("and for
    /// 2025?"). Prefixing the previous question keeps retrieval on topic.</summary>
    private static string RewriteQuery(string question, List<Message> history)
    {
        var isShort = Tokenizer.Tokenize(question).Count <= 4;
        var hasPronoun = Regex.IsMatch(question, @"\b(it|that|this|they|them|those|these|he|she)\b",
            RegexOptions.IgnoreCase);
        if (!isShort && !hasPronoun) return question;

        var lastUser = history.LastOrDefault(m => m.Role == MessageRole.User);
        return lastUser is null ? question : $"{lastUser.Content} {question}";
    }

    /// <summary>Keep only the sources the answer actually cited, so the citation list matches the
    /// bracketed markers the user can see.</summary>
    private static List<CitationDto> FilterToReferenced(string answer, List<CitationDto> citations)
    {
        if (citations.Count == 0) return citations;
        var referenced = Regex.Matches(answer, @"\[(\d{1,2})\]")
            .Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        if (referenced.Count == 0) return citations;
        var filtered = citations.Where(c => referenced.Contains(c.Index)).ToList();
        return filtered.Count > 0 ? filtered : citations;
    }

    private static string[] SuggestFollowUps(
        List<(VectorHit hit, Document doc, KnowledgeBase kb)> hits, string question)
    {
        if (hits.Count == 0) return Array.Empty<string>();
        var asked = Tokenizer.Tokenize(question).ToHashSet();
        var suggestions = new List<string>();

        foreach (var (hit, doc, _) in hits.Take(3))
        {
            var phrase = Tokenizer.Tokenize(hit.Text)
                .Where(t => t.Length > 5 && !asked.Contains(t))
                .GroupBy(t => t).OrderByDescending(g => g.Count())
                .Select(g => g.Key).FirstOrDefault();
            if (phrase is null) continue;
            var candidate = $"What does {doc.FileName} say about {phrase}?";
            if (!suggestions.Contains(candidate)) suggestions.Add(candidate);
        }
        return suggestions.Take(3).ToArray();
    }
}
