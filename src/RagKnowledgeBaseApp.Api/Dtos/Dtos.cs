using RagKnowledgeBaseApp.Api.Domain;

namespace RagKnowledgeBaseApp.Api.Dtos;

// ---------- auth ----------
public record LoginRequest(string Email, string Password);
public record ExternalLoginRequest(string Provider, string IdToken);
public record RegisterRequest(string AccountType, string Email, string Password, string? DisplayName,
    string? CompanyName, string? Slug, string? AllowedEmailDomains);
public record LoginResponse(string AccessToken, DateTime ExpiresAt, MeDto User);
public record MeDto(Guid Id, string Email, string DisplayName, string Role, string TenantName,
    Guid TenantId, string MaxClassification, string? Department, string IdentityProvider,
    string TenantType);

// ---------- tenants and users ----------
public record TenantDto(Guid Id, string Name, string Slug, string? Description,
    string? AllowedEmailDomains, string Type, bool IsActive, DateTime CreatedAt, int UserCount,
    int ChatbotCount, int KnowledgeBaseCount);
public record CreateTenantRequest(string Name, string Slug, string? Description,
    string? AllowedEmailDomains, string? AdminEmail, string? AdminPassword, string? AdminDisplayName);
public record UserDto(Guid Id, Guid TenantId, string Email, string DisplayName, string? Department,
    string Role, string MaxClassification, bool IsActive, DateTime CreatedAt, DateTime? LastLoginAt);
public record CreateUserRequest(string Email, string DisplayName, string? Password, string Role,
    string? Department, string? MaxClassification, Guid? TenantId);
public record UpdateUserRequest(string? DisplayName, string? Role, string? Department,
    string? MaxClassification, bool? IsActive, string? Password);

// ---------- knowledge bases ----------
public record KnowledgeBaseDto(Guid Id, string Name, string? Description, string Scope,
    Guid? OwnerUserId, int ChunkSize, int ChunkOverlap, string EmbeddingModel, bool IsActive,
    DateTime CreatedAt, DateTime? LastIndexedAt, int DocumentCount, int ChunkCount);
public record CreateKnowledgeBaseRequest(string Name, string? Description, string? Scope,
    int? ChunkSize, int? ChunkOverlap, string? EmbeddingModel);
public record UpdateKnowledgeBaseRequest(string? Name, string? Description, int? ChunkSize,
    int? ChunkOverlap, string? EmbeddingModel, bool? IsActive);

// ---------- documents ----------
public record DocumentDto(Guid Id, Guid KnowledgeBaseId, string KnowledgeBaseName, string FileName,
    string ContentType, long SizeBytes, string Status, string? ErrorMessage, int ChunkCount,
    int Version, string Classification, bool IsEphemeral, DateTime CreatedAt, DateTime? IndexedAt,
    string UploadedBy);
public record ChunkDto(Guid Id, int Ordinal, string? Locator, int TokenEstimate, string Preview);

// ---------- chatbots ----------
public record ChatbotDto(Guid Id, string Name, string? Description, string SystemPrompt, string Model,
    double Temperature, int MaxTokens, bool RagEnabled, bool CitationsEnabled, int TopK, int RerankTopN,
    int MaxContextTokens, double SimilarityThreshold, bool HybridSearch, bool QueryRewriting, string ResponseLanguage,
    string WelcomeMessage, string[] SuggestedQuestions, bool AllowUserUpload,
    int ConversationTimeoutMinutes, bool KeepChatHistory, bool IsActive, DateTime CreatedAt,
    KnowledgeBaseLinkDto[] KnowledgeBases, ToolLinkDto[] Tools);
public record KnowledgeBaseLinkDto(Guid KnowledgeBaseId, string Name, int Priority);
public record ToolLinkDto(Guid ToolId, string Name, string Type, int OperationCount);
public record MapToolsRequest(Guid[] ToolIds);
public record SaveChatbotRequest(string Name, string? Description, string? SystemPrompt, string? Model,
    double? Temperature, int? MaxTokens, bool? RagEnabled, bool? CitationsEnabled, int? TopK,
    int? RerankTopN, int? MaxContextTokens, double? SimilarityThreshold, bool? HybridSearch, bool? QueryRewriting,
    string? ResponseLanguage, string? WelcomeMessage, string[]? SuggestedQuestions,
    bool? AllowUserUpload, int? ConversationTimeoutMinutes, bool? KeepChatHistory, bool? IsActive);
public record MapKnowledgeBasesRequest(KnowledgeBaseLinkDto[] KnowledgeBases);

// ---------- chat ----------
public record CitationDto(int Index, Guid DocumentId, string FileName, string? Locator,
    string KnowledgeBase, double Score, string Snippet);
public record ConversationDto(Guid Id, Guid ChatbotId, string ChatbotName, string Title,
    DateTime CreatedAt, DateTime UpdatedAt, int MessageCount);
public record MessageDto(Guid Id, string Role, string Content, CitationDto[] Citations, string? Model,
    int PromptTokens, int CompletionTokens, int LatencyMs, bool NoAnswer, string Feedback,
    DateTime CreatedAt);
public record StartConversationRequest(Guid ChatbotId, string? Title);
public record SendMessageRequest(string Message, Guid[]? AttachmentDocumentIds);
public record ChatResponse(Guid ConversationId, MessageDto Message, string[] FollowUpQuestions,
    /// <summary>Tools the assistant used while answering, and any waiting for approval. The UI
    /// shows these so a tool call is never invisible to the person it was made for.</summary>
    ToolCallDto[] ToolCalls);
public record ToolCallDto(string Tool, string Operation, string Status, string? Error,
    Guid? InvocationId);
public record FeedbackRequest(string Feedback, string? Comment);
public record RenameRequest(string Title);

// ---------- analytics ----------
public record AnalyticsSummaryDto(int Users, int Chatbots, int KnowledgeBases, int Documents,
    int Chunks, int Conversations, int Questions, double SuccessRatePct, double NoAnswerRatePct,
    double AvgResponseTimeSec, long PromptTokens, long CompletionTokens, double EstimatedCostUsd,
    int ThumbsUp, int ThumbsDown, SeriesPointDto[] QuestionsPerDay, NameCountDto[] TopChatbots,
    NameCountDto[] TopKnowledgeBases, int FailedDocuments);
public record SeriesPointDto(string Label, int Value);
public record NameCountDto(string Name, int Count);
public record AuditLogDto(long Id, string? UserEmail, string Action, string? EntityType,
    string? EntityId, string? Details, string? IpAddress, DateTime Timestamp,
    string? TenantName = null);

// ---------- misc ----------
public record ProviderStatusDto(string Llm, string Embeddings, string VectorStore, string Storage,
    string Database, bool LiveLlm, string? Notice);
public record PagedResult<T>(T[] Items, int Total, int Page, int PageSize);

// ---------- tools ----------
public record ToolOperationDto(Guid Id, string Name, string Description, string? HttpMethod,
    string? Path, string ParametersJson, bool IsReadOnly, bool IsActive);

public record ToolDto(Guid Id, string Type, string Name, string Description, string? BaseUrl,
    string? ConnectorApp, string AuthType, string? AuthHeaderName, bool HasSecret,
    string HumanApproval, bool IsActive, string? LastError, DateTime? OperationsRefreshedAt,
    DateTime CreatedAt, ToolOperationDto[] Operations);

public record ToolSaveRequest(string Type, string Name, string Description, string? BaseUrl,
    string? ConnectorApp, string? AuthType, string? AuthHeaderName, string? AuthSecret,
    string? HumanApproval, bool IsActive = true);

public record ToolOperationSaveRequest(string Name, string Description, string? HttpMethod,
    string? Path, string? ParametersJson, bool IsReadOnly, bool IsActive = true);

public record McpImportRequest(string Configuration, string? HumanApproval);
public record McpImportResultDto(int Imported, int Skipped, string[] Names, string[] Warnings);

public record ToolInvocationDto(Guid Id, Guid ToolId, string ToolName, string OperationName,
    string ArgumentsJson, string Status, string? ResultJson, string? Error, int DurationMs,
    Guid? ConversationId, string? UserEmail, DateTime CreatedAt);
