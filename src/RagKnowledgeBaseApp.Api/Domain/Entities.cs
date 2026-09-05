using System.ComponentModel.DataAnnotations;

namespace RagKnowledgeBaseApp.Api.Domain;

/// <summary>A customer company. Every tenant-scoped row carries its TenantId so that
/// retrieval, storage and the API can all filter on it.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(100)] public string Slug { get; set; } = "";
    [MaxLength(500)] public string? Description { get; set; }
    /// <summary>Comma separated email domains that map to this company when someone signs in with
    /// an external identity provider. Empty means SSO users must already exist as accounts.</summary>
    [MaxLength(1000)] public string? AllowedEmailDomains { get; set; }

    public TenantType Type { get; set; } = TenantType.Company;

    /// <summary>The single owner of a Personal tenant. Null for Company tenants.</summary>
    public Guid? OwnerUserId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<User> Users { get; set; } = new();
    public List<KnowledgeBase> KnowledgeBases { get; set; } = new();
    public List<Chatbot> Chatbots { get; set; } = new();
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    [MaxLength(256)] public string Email { get; set; } = "";
    [MaxLength(200)] public string DisplayName { get; set; } = "";
    [MaxLength(200)] public string? Department { get; set; }
    /// <summary>Null for federated (Entra ID / SSO) users.</summary>
    [MaxLength(200)] public string? PasswordHash { get; set; }
    /// <summary>External identity provider subject, when the user came from SSO.</summary>
    [MaxLength(200)] public string? ExternalId { get; set; }
    [MaxLength(50)] public string? IdentityProvider { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    /// <summary>Highest classification this user may retrieve (security trimming).</summary>
    public Classification MaxClassification { get; set; } = Classification.Internal;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public class KnowledgeBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(1000)] public string? Description { get; set; }
    public KnowledgeBaseScope Scope { get; set; } = KnowledgeBaseScope.Company;
    /// <summary>Set for Personal and Conversation scoped knowledge bases.</summary>
    public Guid? OwnerUserId { get; set; }
    public Guid? ConversationId { get; set; }

    // --- RAG configuration (section 13 of the technical document) ---
    public int ChunkSize { get; set; } = 900;
    public int ChunkOverlap { get; set; } = 150;
    [MaxLength(100)] public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastIndexedAt { get; set; }

    public List<Document> Documents { get; set; } = new();
}

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid KnowledgeBaseId { get; set; }
    public KnowledgeBase? KnowledgeBase { get; set; }

    [MaxLength(400)] public string FileName { get; set; } = "";
    [MaxLength(200)] public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>Opaque key handed back by IDocumentStorage (local path or blob name).</summary>
    [MaxLength(1000)] public string StorageKey { get; set; } = "";
    [MaxLength(128)] public string? Sha256 { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;
    [MaxLength(2000)] public string? ErrorMessage { get; set; }
    public int ChunkCount { get; set; }
    public int Version { get; set; } = 1;
    /// <summary>Set when this document replaced an earlier version.</summary>
    public Guid? SupersedesDocumentId { get; set; }

    public Classification Classification { get; set; } = Classification.Internal;
    /// <summary>True for end-user uploads that must never join the company knowledge base.</summary>
    public bool IsEphemeral { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public Guid UploadedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? IndexedAt { get; set; }

    public List<DocumentChunk> Chunks { get; set; } = new();
}

public class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid KnowledgeBaseId { get; set; }
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }

    public int Ordinal { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Page number for PDFs, sheet or slide name for Office files.</summary>
    [MaxLength(200)] public string? Locator { get; set; }
    public int TokenEstimate { get; set; }

    /// <summary>Denormalised copy so retrieval can security-trim without a join.</summary>
    public Classification Classification { get; set; }
    public Guid? OwnerUserId { get; set; }

    /// <summary>Vector serialised little-endian. Kept next to the row so the local vector
    /// store needs no external service; the Qdrant adapter indexes the same values.</summary>
    public byte[] Embedding { get; set; } = Array.Empty<byte>();
}

public class Chatbot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(1000)] public string? Description { get; set; }
    public string SystemPrompt { get; set; } =
        "You are a helpful enterprise assistant. Answer only from the supplied context and always cite your sources.";

    [MaxLength(100)] public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 800;

    public bool RagEnabled { get; set; } = true;
    public bool CitationsEnabled { get; set; } = true;
    public int TopK { get; set; } = 20;
    public int RerankTopN { get; set; } = 5;

    /// <summary>How much retrieved context may be sent to the model, in tokens. This is the real
    /// limit; RerankTopN is only a floor. Modern models have very large windows, so sending five
    /// small chunks of a long document wastes the budget and loses answers that need breadth.</summary>
    public int MaxContextTokens { get; set; } = 12000;
    public double SimilarityThreshold { get; set; } = 0.15;
    public bool HybridSearch { get; set; } = true;
    public bool QueryRewriting { get; set; } = true;

    [MaxLength(50)] public string ResponseLanguage { get; set; } = "auto";
    [MaxLength(1000)] public string WelcomeMessage { get; set; } = "Hello! How can I help you today?";
    /// <summary>Newline separated starter questions shown in the UI.</summary>
    [MaxLength(2000)] public string? SuggestedQuestions { get; set; }
    public bool AllowUserUpload { get; set; } = true;
    public int ConversationTimeoutMinutes { get; set; } = 60;
    public bool KeepChatHistory { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ChatbotKnowledgeBase> KnowledgeBases { get; set; } = new();
    public List<ChatbotTool> Tools { get; set; } = new();
}

/// <summary>Chatbot to knowledge-base mapping, with retrieval priority.</summary>
public class ChatbotKnowledgeBase
{
    public Guid ChatbotId { get; set; }
    public Chatbot? Chatbot { get; set; }
    public Guid KnowledgeBaseId { get; set; }
    public KnowledgeBase? KnowledgeBase { get; set; }
    public int Priority { get; set; } = 1;
}

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid ChatbotId { get; set; }
    public Chatbot? Chatbot { get; set; }
    [MaxLength(300)] public string Title { get; set; } = "New conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }

    public List<Message> Messages { get; set; } = new();
}

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    /// <summary>Serialised citation list, see CitationDto.</summary>
    public string? CitationsJson { get; set; }
    [MaxLength(100)] public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int LatencyMs { get; set; }
    public bool NoAnswer { get; set; }
    public Feedback Feedback { get; set; } = Feedback.None;
    [MaxLength(2000)] public string? FeedbackComment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One row per user per UTC day, holding everything that consumes model quota.
/// Embedding and chat are tracked separately because they bill at very different rates, but the
/// daily limit is enforced against their sum: a user who has spent the day chatting should not
/// then be able to index a large document for free.</summary>
public class DailyTokenUsage
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>UTC calendar day. Deliberately not local time, so the reset point is the same for
    /// every tenant regardless of where its users are.</summary>
    public DateOnly UsageDate { get; set; }

    public long EmbeddingTokens { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }

    public long TotalTokens => EmbeddingTokens + PromptTokens + CompletionTokens;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserEmail { get; set; }
    [MaxLength(100)] public string Action { get; set; } = "";
    [MaxLength(100)] public string? EntityType { get; set; }
    [MaxLength(100)] public string? EntityId { get; set; }
    public string? Details { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>An external capability a chatbot may call: a REST API, a remote MCP server, or a
/// third-party application reached through a connector provider.
///
/// A tool belongs to a tenant and is opt-in per chatbot, so registering one does not by itself let
/// any assistant call it.</summary>
public class Tool
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public ToolType Type { get; set; } = ToolType.Api;
    [MaxLength(120)] public string Name { get; set; } = "";

    /// <summary>Shown to the model, not to the user. This is what the model reads when deciding
    /// whether the tool is relevant, so it carries real weight.</summary>
    [MaxLength(2000)] public string Description { get; set; } = "";

    /// <summary>REST base for an Api tool; the server endpoint for an Mcp tool. Unused by
    /// Connector tools, which are addressed by the provider.</summary>
    [MaxLength(600)] public string? BaseUrl { get; set; }

    /// <summary>Third-party application key for a Connector tool, e.g. GITHUB.</summary>
    [MaxLength(80)] public string? ConnectorApp { get; set; }

    public ToolAuthType AuthType { get; set; } = ToolAuthType.None;
    [MaxLength(80)] public string? AuthHeaderName { get; set; }

    /// <summary>Never returned by the API. Held here rather than in a vault because the platform
    /// already stores its own credentials this way; a deployment handling third-party secrets at
    /// scale should move this to Key Vault and keep only a reference.</summary>
    [MaxLength(2000)] public string? AuthSecret { get; set; }

    public HumanApprovalMode HumanApproval { get; set; } = HumanApprovalMode.Auto;
    public bool IsActive { get; set; } = true;

    /// <summary>Set when a discovery attempt against an MCP server failed, so the UI can explain
    /// why a tool has no operations instead of showing an empty list.</summary>
    [MaxLength(1000)] public string? LastError { get; set; }
    public DateTime? OperationsRefreshedAt { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ToolOperation> Operations { get; set; } = new();
}

/// <summary>One callable function on a tool. Declared by an administrator for an Api tool, and
/// discovered from the server for an Mcp tool.</summary>
public class ToolOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ToolId { get; set; }
    public Tool? Tool { get; set; }

    /// <summary>The function name the model sees. Kept to the character set OpenAI accepts.</summary>
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(1000)] public string Description { get; set; } = "";

    /// <summary>Api tools only.</summary>
    [MaxLength(10)] public string? HttpMethod { get; set; }
    /// <summary>Path appended to the tool's base URL. Placeholders in {braces} are filled from the
    /// arguments the model supplies.</summary>
    [MaxLength(400)] public string? Path { get; set; }

    /// <summary>JSON Schema for the arguments, passed to the model verbatim as the parameter
    /// definition. For MCP this is the server's own inputSchema.</summary>
    public string ParametersJson { get; set; } = """{"type":"object","properties":{}}""";

    /// <summary>Drives Auto approval. Derived from the HTTP method for Api tools and from the
    /// server's readOnlyHint annotation for MCP tools.</summary>
    public bool IsReadOnly { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Chatbot to tool mapping. Registering a tool does not expose it; this does.</summary>
public class ChatbotTool
{
    public Guid ChatbotId { get; set; }
    public Chatbot? Chatbot { get; set; }
    public Guid ToolId { get; set; }
    public Tool? Tool { get; set; }
}

/// <summary>Every attempt to call a tool, including the ones a person refused. This is the record
/// that makes tool use auditable: what was asked, with which arguments, by whom, and what came
/// back.</summary>
public class ToolInvocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ToolId { get; set; }
    public Tool? Tool { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid UserId { get; set; }

    [MaxLength(80)] public string OperationName { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";

    public ToolInvocationStatus Status { get; set; } = ToolInvocationStatus.PendingApproval;

    /// <summary>Truncated before storage: a tool may return a large body, and the audit value is in
    /// what happened rather than in every byte.</summary>
    [MaxLength(8000)] public string? ResultJson { get; set; }
    [MaxLength(2000)] public string? Error { get; set; }

    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
