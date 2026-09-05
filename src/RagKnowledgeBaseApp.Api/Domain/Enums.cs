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

/// <summary>How a tool reaches the outside world. The three kinds differ in how their callable
/// operations are discovered, not in how they are executed once known.</summary>
public enum ToolType
{
    /// <summary>A REST endpoint whose operations an administrator declares by hand.</summary>
    Api = 0,
    /// <summary>A remote Model Context Protocol server that advertises its own tool list.</summary>
    Mcp = 1,
    /// <summary>A third-party application reached through a connector provider.</summary>
    Connector = 2
}

public enum ToolAuthType
{
    None = 0,
    /// <summary>Sent as a named header, e.g. X-Api-Key.</summary>
    ApiKeyHeader = 1,
    /// <summary>Sent as Authorization: Bearer &lt;secret&gt;.</summary>
    BearerToken = 2
}

/// <summary>Whether a person has to confirm a call before it leaves the platform.</summary>
public enum HumanApprovalMode
{
    /// <summary>Read-only operations run unattended; anything that writes needs confirmation.
    /// Read-only is decided by HTTP method for API tools and by the server's own readOnlyHint
    /// annotation for MCP tools.</summary>
    Auto = 0,
    /// <summary>Every call waits for a person, including reads.</summary>
    Always = 1,
    /// <summary>Nothing waits. Only sensible for tools that cannot cause a side effect.</summary>
    Never = 2
}

public enum ToolInvocationStatus
{
    /// <summary>Waiting for a person to approve or reject it.</summary>
    PendingApproval = 0,
    Rejected = 1,
    Succeeded = 2,
    Failed = 3
}
