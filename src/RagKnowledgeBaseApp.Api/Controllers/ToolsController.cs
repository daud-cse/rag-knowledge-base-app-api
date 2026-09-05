using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services;
using RagKnowledgeBaseApp.Api.Services.Tools;

namespace RagKnowledgeBaseApp.Api.Controllers;

/// <summary>Registration and administration of external tools.
///
/// Reading and attaching is a chatbot-administration task, but creating a tool means storing a
/// credential and granting the platform the ability to act on an outside system, so writes are
/// held at company-administrator level.</summary>
[ApiController]
[Route("api/tools")]
[Authorize(Policy = Policies.ChatbotAdmin)]
public class ToolsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _current;
    private readonly AuditService _audit;
    private readonly ToolService _tools;

    public ToolsController(AppDbContext db, CurrentUser current, AuditService audit, ToolService tools)
    {
        _db = db;
        _current = current;
        _audit = audit;
        _tools = tools;
    }

    // ------------------------------- tools -------------------------------

    [HttpGet]
    public async Task<ActionResult<ToolDto[]>> List([FromQuery] string? type, CancellationToken ct)
    {
        var query = _db.Tools.AsNoTracking().Where(t => t.TenantId == _current.TenantId);
        if (Enum.TryParse<ToolType>(type, true, out var wanted))
            query = query.Where(t => t.Type == wanted);

        var tools = await query.Include(t => t.Operations)
            .OrderBy(t => t.Name).AsSplitQuery().ToListAsync(ct);
        return Ok(tools.Select(Map).ToArray());
    }

    /// <summary>The third-party applications a connector may target. Served from the API so every
    /// client sees the same list and the value can be validated on write.</summary>
    [HttpGet("connector-apps")]
    public ActionResult<ConnectorApp[]> ConnectorApps() => Ok(ConnectorCatalog.Apps.ToArray());

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ToolDto>> Get(Guid id, CancellationToken ct)
    {
        var tool = await Find(id, ct);
        return tool is null ? NotFound(new { message = "Tool not found." }) : Ok(Map(tool));
    }

    [HttpPost]
    [Authorize(Policy = Policies.CompanyAdmin)]
    public async Task<ActionResult<ToolDto>> Create(ToolSaveRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<ToolType>(request.Type, true, out var type))
            return BadRequest(new { message = "Type must be Api, Mcp or Connector." });

        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) return BadRequest(new { message = "A tool needs a name." });
        if (string.IsNullOrWhiteSpace(request.Description))
            return BadRequest(new { message = "A description is required — the model reads it to decide when to use the tool." });

        if (await _db.Tools.AnyAsync(t => t.TenantId == _current.TenantId && t.Name == name, ct))
            return BadRequest(new { message = $"A tool called '{name}' already exists." });

        if (type is ToolType.Api or ToolType.Mcp)
        {
            if (!IsUsableUrl(request.BaseUrl))
                return BadRequest(new { message = "A valid http or https URL is required." });
        }
        else if (ConnectorCatalog.Find(request.ConnectorApp) is null)
        {
            return BadRequest(new { message = string.IsNullOrWhiteSpace(request.ConnectorApp)
                ? "Pick an application for this connector."
                : $"'{request.ConnectorApp}' is not an application this platform knows about." });
        }

        var tool = new Tool
        {
            TenantId = _current.TenantId,
            Type = type,
            Name = name,
            Description = request.Description.Trim(),
            BaseUrl = request.BaseUrl?.Trim(),
            ConnectorApp = request.ConnectorApp?.Trim().ToUpperInvariant(),
            AuthType = Enum.TryParse<ToolAuthType>(request.AuthType, true, out var auth) ? auth : ToolAuthType.None,
            AuthHeaderName = request.AuthHeaderName?.Trim(),
            AuthSecret = string.IsNullOrWhiteSpace(request.AuthSecret) ? null : request.AuthSecret,
            HumanApproval = ParseApproval(request.HumanApproval),
            IsActive = request.IsActive,
            CreatedByUserId = _current.Id
        };

        _db.Tools.Add(tool);
        await _db.SaveChangesAsync(ct);

        // An MCP server describes itself, so there is nothing for an administrator to declare.
        if (type == ToolType.Mcp) await _tools.RefreshMcpOperationsAsync(tool, ct);

        await _audit.LogAsync("tool.create", "Tool", tool.Id.ToString(),
            new { tool.Name, Type = type.ToString() }, ct);

        return Ok(Map(await Find(tool.Id, ct) ?? tool));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.CompanyAdmin)]
    public async Task<ActionResult<ToolDto>> Update(Guid id, ToolSaveRequest request, CancellationToken ct)
    {
        var tool = await Find(id, ct);
        if (tool is null) return NotFound(new { message = "Tool not found." });

        if (!string.IsNullOrWhiteSpace(request.Name)) tool.Name = request.Name.Trim();
        if (!string.IsNullOrWhiteSpace(request.Description)) tool.Description = request.Description.Trim();
        if (request.BaseUrl is not null) tool.BaseUrl = request.BaseUrl.Trim();
        if (request.AuthType is not null &&
            Enum.TryParse<ToolAuthType>(request.AuthType, true, out var auth)) tool.AuthType = auth;
        if (request.AuthHeaderName is not null) tool.AuthHeaderName = request.AuthHeaderName.Trim();

        // An empty secret means "leave it alone" rather than "clear it": the UI never receives the
        // stored value, so it cannot send it back on an unrelated edit.
        if (!string.IsNullOrWhiteSpace(request.AuthSecret)) tool.AuthSecret = request.AuthSecret;

        if (request.HumanApproval is not null) tool.HumanApproval = ParseApproval(request.HumanApproval);
        tool.IsActive = request.IsActive;

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tool.update", "Tool", id.ToString(), new { tool.Name }, ct);
        return Ok(Map(tool));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.CompanyAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tool = await Find(id, ct);
        if (tool is null) return NotFound(new { message = "Tool not found." });

        // Invocations are kept deliberately: deleting a tool must not erase the record of what it
        // did. The restrict rule on the foreign key is what forces this to be explicit.
        if (await _db.ToolInvocations.AnyAsync(i => i.ToolId == id, ct))
        {
            tool.IsActive = false;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync("tool.disable", "Tool", id.ToString(), new { tool.Name }, ct);
            return Ok(new { message = "This tool has been used, so it was disabled rather than deleted. Its history is kept." });
        }

        _db.Tools.Remove(tool);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tool.delete", "Tool", id.ToString(), new { tool.Name }, ct);
        return NoContent();
    }

    /// <summary>Re-reads an MCP server's advertised tool list.</summary>
    [HttpPost("{id:guid}/refresh")]
    [Authorize(Policy = Policies.CompanyAdmin)]
    public async Task<ActionResult<ToolDto>> Refresh(Guid id, CancellationToken ct)
    {
        var tool = await Find(id, ct);
        if (tool is null) return NotFound(new { message = "Tool not found." });
        if (tool.Type != ToolType.Mcp)
            return BadRequest(new { message = "Only MCP tools discover their own operations." });

        var (count, error) = await _tools.RefreshMcpOperationsAsync(tool, ct);
        await _audit.LogAsync("tool.refresh", "Tool", id.ToString(), new { tool.Name, count }, ct);

        return error is null
            ? Ok(Map(await Find(id, ct) ?? tool))
            : BadRequest(new { message = $"Could not reach the MCP server: {error}" });
    }

    // ---------------------------- MCP import -----------------------------

    /// <summary>Imports a Claude-style <c>mcpServers</c> block. Every remote server in it becomes
    /// its own tool. Servers defined by a command are skipped: running a process on behalf of a
    /// tenant is not something a hosted multi-tenant platform should do.</summary>
    [HttpPost("import-mcp")]
    [Authorize(Policy = Policies.CompanyAdmin)]
    public async Task<ActionResult<McpImportResultDto>> ImportMcp(McpImportRequest request, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(request.Configuration ?? "");
        }
        catch (JsonException ex)
        {
            return BadRequest(new { message = $"That is not valid JSON: {ex.Message}" });
        }

        // Accept both the wrapped form and a bare map of servers.
        if (!root.TryGetProperty("mcpServers", out var servers))
            servers = root;
        if (servers.ValueKind != JsonValueKind.Object)
            return BadRequest(new { message = "Expected an object of servers, optionally wrapped in \"mcpServers\"." });

        var approval = ParseApproval(request.HumanApproval);
        var imported = new List<string>();
        var warnings = new List<string>();
        var skipped = 0;

        foreach (var server in servers.EnumerateObject())
        {
            var name = server.Name.Trim();
            var url = server.Value.TryGetProperty("url", out var u) ? u.GetString() : null;

            if (string.IsNullOrWhiteSpace(url))
            {
                skipped++;
                warnings.Add($"'{name}' has no url — local command servers are not supported.");
                continue;
            }
            if (!IsUsableUrl(url))
            {
                skipped++;
                warnings.Add($"'{name}' has a url that is not http or https.");
                continue;
            }
            if (await _db.Tools.AnyAsync(t => t.TenantId == _current.TenantId && t.Name == name, ct))
            {
                skipped++;
                warnings.Add($"'{name}' already exists and was left alone.");
                continue;
            }

            // Headers carry the credential. Authorization: Bearer is by far the common case, so it
            // is recognised; any other single header is stored as a named API key.
            var (authType, headerName, secret) = ReadHeaders(server.Value);

            var tool = new Tool
            {
                TenantId = _current.TenantId,
                Type = ToolType.Mcp,
                Name = name,
                Description = $"MCP server imported from configuration ({url}).",
                BaseUrl = url,
                AuthType = authType,
                AuthHeaderName = headerName,
                AuthSecret = secret,
                HumanApproval = approval,
                CreatedByUserId = _current.Id
            };
            _db.Tools.Add(tool);
            await _db.SaveChangesAsync(ct);

            var (count, error) = await _tools.RefreshMcpOperationsAsync(tool, ct);
            if (error is not null)
                warnings.Add($"'{name}' was created but could not be reached: {error}");
            else if (count == 0)
                warnings.Add($"'{name}' was reached but advertises no tools.");

            imported.Add(name);
        }

        if (imported.Count > 0)
            await _audit.LogAsync("tool.import-mcp", "Tool", null,
                new { Count = imported.Count, Names = imported }, ct);

        return Ok(new McpImportResultDto(imported.Count, skipped, imported.ToArray(), warnings.ToArray()));
    }

    // ---------------------------- operations -----------------------------

    /// <summary>Declares a callable operation on an API tool. MCP tools discover their own.</summary>
    [HttpPost("{id:guid}/operations")]
    [Authorize(Policy = Policies.CompanyAdmin)]
    public async Task<ActionResult<ToolDto>> AddOperation(Guid id, ToolOperationSaveRequest request,
        CancellationToken ct)
    {
        var tool = await Find(id, ct);
        if (tool is null) return NotFound(new { message = "Tool not found." });
        if (tool.Type == ToolType.Mcp)
            return BadRequest(new { message = "MCP tools take their operations from the server. Use refresh instead." });

        var name = McpToolExecutor.Sanitise((request.Name ?? "").Trim());
        if (name.Length == 0) return BadRequest(new { message = "An operation needs a name." });
        if (tool.Operations.Any(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { message = $"'{name}' already exists on this tool." });

        if (!IsValidSchema(request.ParametersJson, out var schemaError))
            return BadRequest(new { message = $"The parameter schema is not valid JSON: {schemaError}" });

        var method = (request.HttpMethod ?? "GET").Trim().ToUpperInvariant();
        _db.ToolOperations.Add(new ToolOperation
        {
            ToolId = tool.Id,
            Name = name,
            Description = (request.Description ?? "").Trim(),
            HttpMethod = method,
            Path = request.Path?.Trim(),
            ParametersJson = string.IsNullOrWhiteSpace(request.ParametersJson)
                ? """{"type":"object","properties":{}}""" : request.ParametersJson,
            // A GET or HEAD cannot change anything, which is what Auto approval keys off.
            IsReadOnly = request.IsReadOnly || method is "GET" or "HEAD",
            IsActive = request.IsActive
        });
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tool.operation-add", "Tool", id.ToString(), new { tool.Name, name }, ct);

        return Ok(Map(await Find(id, ct) ?? tool));
    }

    [HttpDelete("{id:guid}/operations/{operationId:guid}")]
    [Authorize(Policy = Policies.CompanyAdmin)]
    public async Task<IActionResult> DeleteOperation(Guid id, Guid operationId, CancellationToken ct)
    {
        var tool = await Find(id, ct);
        if (tool is null) return NotFound(new { message = "Tool not found." });

        var operation = tool.Operations.FirstOrDefault(o => o.Id == operationId);
        if (operation is null) return NotFound(new { message = "Operation not found." });

        _db.ToolOperations.Remove(operation);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tool.operation-remove", "Tool", id.ToString(),
            new { Tool = tool.Name, Operation = operation.Name }, ct);
        return NoContent();
    }

    // ---------------------------- invocations ----------------------------

    /// <summary>Calls waiting for a person, and recent history. A chatbot administrator sees the
    /// whole tenant; anyone else sees only their own.</summary>
    [HttpGet("invocations")]
    [Authorize]
    public async Task<ActionResult<ToolInvocationDto[]>> Invocations([FromQuery] string? status,
        CancellationToken ct)
    {
        var query = _db.ToolInvocations.AsNoTracking()
            .Where(i => i.TenantId == _current.TenantId);

        if (!_current.IsAtLeast(UserRole.ChatbotAdmin))
            query = query.Where(i => i.UserId == _current.Id);
        if (Enum.TryParse<ToolInvocationStatus>(status, true, out var wanted))
            query = query.Where(i => i.Status == wanted);

        var rows = await query.OrderByDescending(i => i.CreatedAt).Take(200)
            .Join(_db.Tools, i => i.ToolId, t => t.Id, (i, t) => new { i, t.Name })
            .ToListAsync(ct);

        var userIds = rows.Select(r => r.i.UserId).Distinct().ToList();
        var emails = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        return Ok(rows.Select(r => new ToolInvocationDto(r.i.Id, r.i.ToolId, r.Name,
            r.i.OperationName, r.i.ArgumentsJson, r.i.Status.ToString(), r.i.ResultJson, r.i.Error,
            r.i.DurationMs, r.i.ConversationId, emails.GetValueOrDefault(r.i.UserId),
            r.i.CreatedAt)).ToArray());
    }

    /// <summary>Approves a queued call and runs it. The person who asked the question is the human
    /// in the loop, so they may approve their own; an administrator may approve anyone's.</summary>
    [HttpPost("invocations/{id:guid}/approve")]
    [Authorize]
    public async Task<ActionResult<ToolInvocationDto>> Approve(Guid id, CancellationToken ct)
    {
        var invocation = await _db.ToolInvocations
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == _current.TenantId, ct);
        if (invocation is null) return NotFound(new { message = "Nothing to approve." });
        if (invocation.UserId != _current.Id && !_current.IsAtLeast(UserRole.ChatbotAdmin))
            return Forbid();
        if (invocation.Status != ToolInvocationStatus.PendingApproval)
            return BadRequest(new { message = $"This call is already {invocation.Status}." });

        var tool = await Find(invocation.ToolId, ct);
        var operation = tool?.Operations.FirstOrDefault(o =>
            o.Name.Equals(invocation.OperationName, StringComparison.OrdinalIgnoreCase));
        if (tool is null || operation is null)
            return BadRequest(new { message = "The tool or operation no longer exists." });

        var started = DateTime.UtcNow;
        var executor = HttpContext.RequestServices.GetServices<IToolExecutor>()
            .FirstOrDefault(e => e.Handles == tool.Type);
        var result = executor is null
            ? new ToolExecutionResult(false, "", $"No executor is registered for {tool.Type} tools.")
            : await executor.ExecuteAsync(tool, operation, invocation.ArgumentsJson, ct);

        invocation.Status = result.Success ? ToolInvocationStatus.Succeeded : ToolInvocationStatus.Failed;
        invocation.ResultJson = result.Content.Length > 8000 ? result.Content[..8000] : result.Content;
        invocation.Error = result.Error;
        invocation.DecidedByUserId = _current.Id;
        invocation.DecidedAt = DateTime.UtcNow;
        invocation.DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("tool.approve", "ToolInvocation", id.ToString(),
            new { tool.Name, invocation.OperationName, result.Success }, ct);

        return Ok(new ToolInvocationDto(invocation.Id, tool.Id, tool.Name, invocation.OperationName,
            invocation.ArgumentsJson, invocation.Status.ToString(), invocation.ResultJson,
            invocation.Error, invocation.DurationMs, invocation.ConversationId, _current.Email,
            invocation.CreatedAt));
    }

    [HttpPost("invocations/{id:guid}/reject")]
    [Authorize]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var invocation = await _db.ToolInvocations
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == _current.TenantId, ct);
        if (invocation is null) return NotFound(new { message = "Nothing to reject." });
        if (invocation.UserId != _current.Id && !_current.IsAtLeast(UserRole.ChatbotAdmin))
            return Forbid();
        if (invocation.Status != ToolInvocationStatus.PendingApproval)
            return BadRequest(new { message = $"This call is already {invocation.Status}." });

        invocation.Status = ToolInvocationStatus.Rejected;
        invocation.DecidedByUserId = _current.Id;
        invocation.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("tool.reject", "ToolInvocation", id.ToString(), null, ct);
        return NoContent();
    }

    // ------------------------------ helpers ------------------------------

    private Task<Tool?> Find(Guid id, CancellationToken ct) =>
        _db.Tools.Include(t => t.Operations)
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == _current.TenantId, ct);

    private static HumanApprovalMode ParseApproval(string? value) =>
        Enum.TryParse<HumanApprovalMode>(value, true, out var mode) ? mode : HumanApprovalMode.Auto;

    /// <summary>Rejects anything that is not an absolute http(s) URL, which keeps file:// and
    /// other schemes out of a field that is later handed to an HTTP client.</summary>
    private static bool IsUsableUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsValidSchema(string? json, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            JsonSerializer.Deserialize<JsonElement>(json);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static (ToolAuthType, string?, string?) ReadHeaders(JsonElement server)
    {
        if (!server.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
            return (ToolAuthType.None, null, null);

        foreach (var header in headers.EnumerateObject())
        {
            var value = header.Value.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (header.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? (ToolAuthType.BearerToken, null, value["Bearer ".Length..].Trim())
                    : (ToolAuthType.ApiKeyHeader, header.Name, value);

            return (ToolAuthType.ApiKeyHeader, header.Name, value);
        }
        return (ToolAuthType.None, null, null);
    }

    private static ToolDto Map(Tool t) => new(
        t.Id, t.Type.ToString(), t.Name, t.Description, t.BaseUrl, t.ConnectorApp,
        t.AuthType.ToString(), t.AuthHeaderName,
        // The stored secret never leaves the server; the UI only needs to know one is set.
        !string.IsNullOrWhiteSpace(t.AuthSecret),
        t.HumanApproval.ToString(), t.IsActive, t.LastError, t.OperationsRefreshedAt, t.CreatedAt,
        t.Operations.OrderBy(o => o.Name).Select(o => new ToolOperationDto(o.Id, o.Name,
            o.Description, o.HttpMethod, o.Path, o.ParametersJson, o.IsReadOnly, o.IsActive)).ToArray());
}
