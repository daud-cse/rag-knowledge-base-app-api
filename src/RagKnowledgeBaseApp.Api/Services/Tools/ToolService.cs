using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Services.Llm;

namespace RagKnowledgeBaseApp.Api.Services.Tools;

/// <summary>What the caller must do with a call the model asked for.</summary>
public enum ToolDecision { Run, NeedsApproval, Unknown }

public record ResolvedCall(ToolDecision Decision, Tool? Tool, ToolOperation? Operation);

/// <summary>Turns a chatbot's attached tools into definitions the model can call, decides which
/// calls may run unattended, executes them and records every attempt.</summary>
public class ToolService
{
    /// <summary>How many times the model may call tools before it must answer. Without a ceiling a
    /// model that keeps calling the same failing tool would loop until the request times out.</summary>
    public const int MaxRounds = 3;

    private readonly AppDbContext _db;
    private readonly IEnumerable<IToolExecutor> _executors;
    private readonly ILogger<ToolService> _logger;

    public ToolService(AppDbContext db, IEnumerable<IToolExecutor> executors, ILogger<ToolService> logger)
    {
        _db = db;
        _executors = executors;
        _logger = logger;
    }

    /// <summary>Active tools attached to this chatbot, with their active operations.</summary>
    public async Task<List<Tool>> ForChatbotAsync(Guid chatbotId, Guid tenantId, CancellationToken ct = default)
    {
        // Queried from Tools rather than through the mapping table: EF cannot apply Include after a
        // Select that projects through a navigation.
        var attached = _db.ChatbotTools.Where(m => m.ChatbotId == chatbotId).Select(m => m.ToolId);

        var tools = await _db.Tools.AsNoTracking()
            .Where(t => attached.Contains(t.Id) && t.TenantId == tenantId && t.IsActive)
            .Include(t => t.Operations)
            .AsSplitQuery()
            .ToListAsync(ct);

        // Filtered in memory: a filtered Include cannot be combined with AsNoTracking on all
        // providers, and a tool has few enough operations for this to be free.
        foreach (var tool in tools)
            tool.Operations = tool.Operations.Where(o => o.IsActive).ToList();

        return tools;
    }

    /// <summary>The function list handed to the model. Names are prefixed with the tool so two
    /// tools exposing a "search" operation stay distinguishable, and so a call can be routed back
    /// to its tool without a second lookup.</summary>
    public static List<ToolDefinition> Describe(IEnumerable<Tool> tools)
    {
        var definitions = new List<ToolDefinition>();
        foreach (var tool in tools)
            foreach (var op in tool.Operations)
                definitions.Add(new ToolDefinition(
                    QualifiedName(tool, op),
                    string.IsNullOrWhiteSpace(op.Description)
                        ? $"{op.Name} on {tool.Name}. {tool.Description}"
                        : $"{op.Description} (via {tool.Name})",
                    op.ParametersJson));
        return definitions;
    }

    public static string QualifiedName(Tool tool, ToolOperation op)
        => McpToolExecutor.Sanitise($"{tool.Name}__{op.Name}");

    /// <summary>Matches a call the model asked for back to its operation, and applies the tool's
    /// approval mode.</summary>
    public ResolvedCall Resolve(IEnumerable<Tool> tools, string functionName)
    {
        foreach (var tool in tools)
            foreach (var op in tool.Operations)
            {
                if (!string.Equals(QualifiedName(tool, op), functionName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var decision = tool.HumanApproval switch
                {
                    HumanApprovalMode.Never => ToolDecision.Run,
                    HumanApprovalMode.Always => ToolDecision.NeedsApproval,
                    // Auto: a read cannot change anything, so it runs; anything else waits.
                    _ => op.IsReadOnly ? ToolDecision.Run : ToolDecision.NeedsApproval
                };
                return new ResolvedCall(decision, tool, op);
            }

        return new ResolvedCall(ToolDecision.Unknown, null, null);
    }

    /// <summary>Runs the call and records it. The invocation row is written whether the call
    /// succeeded or not, because a failed attempt is exactly what an audit needs to show.</summary>
    public async Task<ToolExecutionResult> ExecuteAsync(Tool tool, ToolOperation operation,
        string argumentsJson, Guid tenantId, Guid userId, Guid? conversationId,
        CancellationToken ct = default)
    {
        var executor = _executors.FirstOrDefault(e => e.Handles == tool.Type);
        if (executor is null)
        {
            var unsupported = tool.Type == ToolType.Connector
                ? "Connector tools need a connector provider account, which is not configured."
                : $"No executor is registered for {tool.Type} tools.";
            await RecordAsync(tool, operation, argumentsJson, tenantId, userId, conversationId,
                ToolInvocationStatus.Failed, null, unsupported, 0, ct);
            return new ToolExecutionResult(false, "", unsupported);
        }

        var started = DateTime.UtcNow;
        var result = await executor.ExecuteAsync(tool, operation, argumentsJson, ct);
        var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;

        await RecordAsync(tool, operation, argumentsJson, tenantId, userId, conversationId,
            result.Success ? ToolInvocationStatus.Succeeded : ToolInvocationStatus.Failed,
            result.Content, result.Error, ms, ct);

        return result;
    }

    /// <summary>Parks a call that needs a person to confirm it.</summary>
    public async Task<ToolInvocation> RequestApprovalAsync(Tool tool, ToolOperation operation,
        string argumentsJson, Guid tenantId, Guid userId, Guid? conversationId,
        CancellationToken ct = default)
        => await RecordAsync(tool, operation, argumentsJson, tenantId, userId, conversationId,
            ToolInvocationStatus.PendingApproval, null, null, 0, ct);

    private async Task<ToolInvocation> RecordAsync(Tool tool, ToolOperation operation,
        string argumentsJson, Guid tenantId, Guid userId, Guid? conversationId,
        ToolInvocationStatus status, string? result, string? error, int ms, CancellationToken ct)
    {
        var invocation = new ToolInvocation
        {
            TenantId = tenantId,
            ToolId = tool.Id,
            ConversationId = conversationId,
            UserId = userId,
            OperationName = operation.Name,
            ArgumentsJson = Cap(argumentsJson, 4000),
            Status = status,
            ResultJson = result is null ? null : Cap(result, 8000),
            Error = error is null ? null : Cap(error, 2000),
            DurationMs = ms
        };
        _db.ToolInvocations.Add(invocation);
        await _db.SaveChangesAsync(ct);
        return invocation;
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Re-reads an MCP server's tool list and replaces the stored operations with it.
    /// Operations are replaced wholesale rather than merged: a tool the server has withdrawn must
    /// stop being offered to the model.</summary>
    public async Task<(int Count, string? Error)> RefreshMcpOperationsAsync(Tool tool,
        CancellationToken ct = default)
    {
        var mcp = _executors.OfType<McpToolExecutor>().FirstOrDefault();
        if (mcp is null) return (0, "MCP support is not registered.");

        var (operations, error) = await mcp.DiscoverAsync(tool, ct);
        if (error is not null)
        {
            tool.LastError = error;
            await _db.SaveChangesAsync(ct);
            return (0, error);
        }

        await _db.ToolOperations.Where(o => o.ToolId == tool.Id).ExecuteDeleteAsync(ct);
        _db.ToolOperations.AddRange(operations);
        tool.LastError = null;
        tool.OperationsRefreshedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Discovered {Count} operations on MCP tool {Tool}", operations.Count, tool.Name);
        return (operations.Count, null);
    }
}
