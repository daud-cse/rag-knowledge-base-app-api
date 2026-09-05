using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RagKnowledgeBaseApp.Api.Domain;

namespace RagKnowledgeBaseApp.Api.Services.Tools;

public record ToolExecutionResult(bool Success, string Content, string? Error = null);

/// <summary>Runs one operation on one tool. Implementations differ only in how they address the
/// remote end; everything about approval, auditing and quota sits above this.</summary>
public interface IToolExecutor
{
    ToolType Handles { get; }
    Task<ToolExecutionResult> ExecuteAsync(Tool tool, ToolOperation operation, string argumentsJson,
        CancellationToken ct = default);
}

/// <summary>Shared plumbing: applying a tool's credential, and keeping a response small enough to
/// hand back to a model without blowing the context budget.</summary>
public static class ToolHttp
{
    /// <summary>A tool that returns a megabyte of JSON would cost more than the answer is worth,
    /// so the body is cut. The model is told the cut happened rather than being left to infer it
    /// from truncated JSON.</summary>
    public const int MaxResponseChars = 6000;

    public static void ApplyAuth(HttpRequestMessage request, Tool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.AuthSecret)) return;

        switch (tool.AuthType)
        {
            case ToolAuthType.BearerToken:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tool.AuthSecret);
                break;
            case ToolAuthType.ApiKeyHeader when !string.IsNullOrWhiteSpace(tool.AuthHeaderName):
                request.Headers.TryAddWithoutValidation(tool.AuthHeaderName, tool.AuthSecret);
                break;
        }
    }

    public static string Truncate(string body) =>
        body.Length <= MaxResponseChars
            ? body
            : body[..MaxResponseChars] + "\n\n[response truncated after "
              + MaxResponseChars + " characters]";
}

/// <summary>Calls a REST endpoint declared by an administrator.
///
/// Arguments the model supplies are used in three places: to fill {placeholders} in the path, as
/// the query string for methods without a body, and as the JSON body for methods with one.</summary>
public class ApiToolExecutor : IToolExecutor
{
    private readonly HttpClient _http;
    private readonly ILogger<ApiToolExecutor> _logger;

    public ApiToolExecutor(HttpClient http, ILogger<ApiToolExecutor> logger)
    {
        _http = http;
        _logger = logger;
    }

    public ToolType Handles => ToolType.Api;

    public async Task<ToolExecutionResult> ExecuteAsync(Tool tool, ToolOperation operation,
        string argumentsJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tool.BaseUrl))
            return new ToolExecutionResult(false, "", "This tool has no base URL configured.");

        Dictionary<string, JsonElement> args;
        try
        {
            args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) ?? new();
        }
        catch (JsonException ex)
        {
            return new ToolExecutionResult(false, "", $"The model sent malformed arguments: {ex.Message}");
        }

        var method = new HttpMethod((operation.HttpMethod ?? "GET").ToUpperInvariant());
        var path = operation.Path ?? "";
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Path placeholders first, so a value used in the path is not repeated in the query string.
        foreach (var (key, value) in args)
        {
            var token = "{" + key + "}";
            if (!path.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
            path = path.Replace(token, Uri.EscapeDataString(Stringify(value)),
                StringComparison.OrdinalIgnoreCase);
            used.Add(key);
        }

        var url = tool.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        var hasBody = method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch;

        if (!hasBody)
        {
            var query = args.Where(a => !used.Contains(a.Key))
                .Select(a => Uri.EscapeDataString(a.Key) + "=" + Uri.EscapeDataString(Stringify(a.Value)))
                .ToList();
            if (query.Count > 0) url += (url.Contains('?') ? "&" : "?") + string.Join("&", query);
        }

        using var request = new HttpRequestMessage(method, url);
        ToolHttp.ApplyAuth(request, tool);
        if (hasBody)
        {
            var body = JsonSerializer.Serialize(args.Where(a => !used.Contains(a.Key))
                .ToDictionary(a => a.Key, a => a.Value));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            // A failing status is returned to the model rather than thrown: "404 not found" is
            // often the correct answer to give the person, not an error to hide.
            return new ToolExecutionResult(response.IsSuccessStatusCode,
                ToolHttp.Truncate(string.IsNullOrWhiteSpace(body)
                    ? $"HTTP {(int)response.StatusCode} with an empty body."
                    : body),
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API tool {Tool} operation {Operation} failed", tool.Name, operation.Name);
            return new ToolExecutionResult(false, "", ex.Message);
        }
    }

    private static string Stringify(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => e.GetRawText()
    };
}

/// <summary>Talks to a remote Model Context Protocol server over HTTP using JSON-RPC.
///
/// Only remote servers are supported. A local MCP server is launched as a child process on the
/// machine hosting it, which in a multi-tenant platform would mean letting one tenant run commands
/// on shared infrastructure.</summary>
public class McpToolExecutor : IToolExecutor
{
    private readonly HttpClient _http;
    private readonly ILogger<McpToolExecutor> _logger;

    public McpToolExecutor(HttpClient http, ILogger<McpToolExecutor> logger)
    {
        _http = http;
        _logger = logger;
    }

    public ToolType Handles => ToolType.Mcp;

    public async Task<ToolExecutionResult> ExecuteAsync(Tool tool, ToolOperation operation,
        string argumentsJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tool.BaseUrl))
            return new ToolExecutionResult(false, "", "This MCP server has no URL configured.");

        try
        {
            JsonElement arguments;
            try
            {
                arguments = JsonSerializer.Deserialize<JsonElement>(
                    string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            }
            catch (JsonException ex)
            {
                return new ToolExecutionResult(false, "", $"The model sent malformed arguments: {ex.Message}");
            }

            using var doc = await RpcAsync(tool, "tools/call", new
            {
                name = operation.Name,
                arguments
            }, ct);

            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                return new ToolExecutionResult(false, "",
                    error.TryGetProperty("message", out var m) ? m.GetString() ?? "MCP error" : "MCP error");

            if (!root.TryGetProperty("result", out var result))
                return new ToolExecutionResult(false, "", "The MCP server returned no result.");

            // A server may flag a failure inside a successful envelope.
            var isError = result.TryGetProperty("isError", out var e) &&
                          e.ValueKind == JsonValueKind.True;

            return new ToolExecutionResult(!isError, ToolHttp.Truncate(FlattenContent(result)),
                isError ? "The MCP server reported an error." : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP tool {Tool} operation {Operation} failed", tool.Name, operation.Name);
            return new ToolExecutionResult(false, "", ex.Message);
        }
    }

    /// <summary>Asks the server what it can do. Used when a tool is registered and whenever an
    /// administrator refreshes it, because a server's tool list can change under us.</summary>
    public async Task<(List<ToolOperation> Operations, string? Error)> DiscoverAsync(Tool tool,
        CancellationToken ct = default)
    {
        try
        {
            using var doc = await RpcAsync(tool, "tools/list", new { }, ct);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
                return (new(), error.TryGetProperty("message", out var m)
                    ? m.GetString() ?? "MCP error" : "MCP error");

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                return (new(), "The server did not return a tool list.");

            var operations = new List<ToolOperation>();
            foreach (var t in tools.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;

                // readOnlyHint is what makes Auto approval meaningful for MCP: the server tells us
                // which of its tools cannot change anything.
                var readOnly = t.TryGetProperty("annotations", out var ann) &&
                               ann.TryGetProperty("readOnlyHint", out var ro) &&
                               ro.ValueKind == JsonValueKind.True;

                operations.Add(new ToolOperation
                {
                    ToolId = tool.Id,
                    Name = Sanitise(name),
                    Description = t.TryGetProperty("description", out var d)
                        ? Trim(d.GetString() ?? "", 1000) : "",
                    ParametersJson = t.TryGetProperty("inputSchema", out var schema)
                        ? schema.GetRawText()
                        : """{"type":"object","properties":{}}""",
                    IsReadOnly = readOnly,
                    IsActive = true
                });
            }
            return (operations, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP discovery failed for {Tool}", tool.Name);
            return (new(), ex.Message);
        }
    }

    private async Task<JsonDocument> RpcAsync(Tool tool, string method, object parameters,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tool.BaseUrl);
        ToolHttp.ApplyAuth(request, tool);
        // Streamable HTTP transport may answer with either JSON or an SSE stream; asking for both
        // keeps compliant servers from refusing outright.
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("n"),
            method,
            @params = parameters
        }), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(ExtractJson(body));
    }

    /// <summary>Servers using the SSE flavour of the transport answer with "data:" framing around
    /// the JSON payload. Unwrapping it here keeps the caller free of transport detail.</summary>
    private static string ExtractJson(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return trimmed;

        foreach (var line in body.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = l[5..].Trim();
                if (payload.StartsWith('{')) return payload;
            }
        }
        return trimmed;
    }

    /// <summary>MCP results are a list of typed content blocks. The model only needs the text.</summary>
    private static string FlattenContent(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return result.GetRawText();

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                sb.AppendLine(text.GetString());
            else
                sb.AppendLine(block.GetRawText());
        }
        var flat = sb.ToString().Trim();
        return flat.Length == 0 ? result.GetRawText() : flat;
    }

    /// <summary>OpenAI accepts function names matching [a-zA-Z0-9_-]{1,64}; MCP names are looser.</summary>
    public static string Sanitise(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')
            .ToArray());
        return cleaned.Length <= 64 ? cleaned : cleaned[..64];
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
