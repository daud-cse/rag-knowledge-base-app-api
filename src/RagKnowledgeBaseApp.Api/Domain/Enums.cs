namespace RagKnowledgeBaseApp.Api.Domain;

/// <summary>Role hierarchy used for RBAC. Higher value = more privilege.</summary>
public enum UserRole
{
    User = 10,
    ChatbotAdmin = 20,
    KnowledgeAdmin = 30,
    CompanyAdmin = 40,
    SuperAdmin = 50
}

/// <summary>How a tenant came into existence, and therefore how it behaves.</summary>
public enum TenantType
{
    /// <summary>An organisation. An administrator adds users, chatbots and knowledge bases.</summary>
    Company = 0,

    /// <summary>One person's own workspace, created automatically when an individual signs up.
    /// It holds exactly one user, who owns everything in it.</summary>
    Personal = 1
}

/// <summary>Data classification used for document-level security trimming.</summary>
public enum Classification
{
    Public = 0,
    Internal = 10,
    Confidential = 20,
    Restricted = 30
}

public enum KnowledgeBaseScope
{
    /// <summary>Shared across the whole tenant.</summary>
    Company = 0,
    /// <summary>Private to a single user.</summary>
    Personal = 1,
    /// <summary>Auto-created holder for files a user attaches to one conversation.</summary>
    Conversation = 2
}

public enum DocumentStatus
{
    Uploaded = 0,
    Validating = 1,
    Extracting = 2,
    Chunking = 3,
    Embedding = 4,
    Indexed = 5,
    Failed = 6,
    Archived = 7
}

public enum MessageRole
{
    System = 0,
    User = 1,
    Assistant = 2
}

public enum Feedback
{
    None = 0,
    ThumbsUp = 1,
    ThumbsDown = 2
}
