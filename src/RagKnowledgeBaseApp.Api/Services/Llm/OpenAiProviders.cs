using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RagKnowledgeBaseApp.Api.Services.Llm;

/// <summary>Shared HTTP plumbing for OpenAI and Azure OpenAI, which differ only in URL shape
/// and how the key is presented.</summary>
public abstract class OpenAiClientBase
{
    protected readonly HttpClient Http;
    protected readonly LlmOptions Options;

    protected OpenAiClientBase(HttpClient http, LlmOptions options)
    {
        Http = http;
        Options = options;
        if (IsAzure)
            Http.DefaultRequestHeaders.TryAddWithoutValidation("api-key", options.ApiKey);
        else
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    protected bool IsAzure => Options.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase);

    protected string ChatUrl(string model) => IsAzure
        ? $"{Options.Endpoint.TrimEnd('/')}/openai/deployments/{Deployment(Options.ChatDeployment, model)}/chat/completions?api-version={Options.ApiVersion}"
        : $"{Options.Endpoint.TrimEnd('/')}/chat/completions";

    protected string EmbeddingUrl(string model) => IsAzure
        ? $"{Options.Endpoint.TrimEnd('/')}/openai/deployments/{Deployment(Options.EmbeddingDeployment, model)}/embeddings?api-version={Options.ApiVersion}"
        : $"{Options.Endpoint.TrimEnd('/')}/embeddings";

    private static string Deployment(string configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    protected async Task<JsonDocument> PostAsync(string url, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} from LLM provider: {Trim(body)}");
        return JsonDocument.Parse(body);
    }

    private static string Trim(string s) => s.Length <= 500 ? s : s[..500];
}

public class OpenAiEmbeddingProvider : OpenAiClientBase, IEmbeddingProvider
{
    private readonly ILogger<OpenAiEmbeddingProvider> _logger;

    public OpenAiEmbeddingProvider(HttpClient http, LlmOptions options,
        ILogger<OpenAiEmbeddingProvider> logger) : base(http, options) => _logger = logger;

    public string ProviderName => IsAzure ? $"Azure OpenAI ({Options.EmbeddingModel})" : $"OpenAI ({Options.EmbeddingModel})";
    public bool IsLive => true;
    public int Dimensions => Options.EmbeddingDimensions > 0 ? Options.EmbeddingDimensions : 1536;

    public async Task<float[]> EmbedAsync(string text, string model, CancellationToken ct = default)
        => (await EmbedBatchAsync(new[] { text }, model, ct))[0];

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, string model,
        CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? Options.EmbeddingModel : model;
        // The text-embedding-3 family can return a shortened vector; older models cannot, so the
        // parameter is only sent when it would actually change the width.
        object payload = effectiveModel.StartsWith("text-embedding-3", StringComparison.OrdinalIgnoreCase)
            ? new { model = effectiveModel, input = texts, dimensions = Dimensions }
            : new { model = effectiveModel, input = texts };

        try
        {
            using var doc = await PostAsync(EmbeddingUrl(effectiveModel), payload, ct);
            var data = doc.RootElement.GetProperty("data");
            var result = new List<float[]>(texts.Count);
            foreach (var item in data.EnumerateArray())
            {
                var vector = item.GetProperty("embedding").EnumerateArray()
                    .Select(v => (float)v.GetDouble()).ToArray();
                if (vector.Length != Dimensions)
                    throw new InvalidOperationException(
                        $"{effectiveModel} returned {vector.Length}-dimensional vectors but the index " +
                        $"expects {Dimensions}. Set Llm:EmbeddingDimensions to {vector.Length} and re-index.");
                result.Add(VectorMath.Normalize(vector));
            }
            return result;
        }
        catch (Exception ex)
        {
            // Deliberately not falling back to the local embedder: its vectors live in a different
            // space, and mixing them into the index would silently poison retrieval. Fail the
            // document instead, so it shows as Failed with a reason the operator can act on.
            _logger.LogError(ex, "Embedding call failed for model {Model}", effectiveModel);
            throw;
        }
    }
}

public class OpenAiChatCompletionProvider : OpenAiClientBase, IChatCompletionProvider
{
    private readonly ILogger<OpenAiChatCompletionProvider> _logger;

    public OpenAiChatCompletionProvider(HttpClient http, LlmOptions options,
        ILogger<OpenAiChatCompletionProvider> logger) : base(http, options) => _logger = logger;

    public string ProviderName => IsAzure ? "Azure OpenAI" : "OpenAI";
    public bool IsLive => true;

    public async Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request,
        CancellationToken ct = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? Options.ChatModel : request.Model;
        var messages = new List<object> { new { role = "system", content = BuildSystemPrompt(request) } };
        foreach (var turn in request.History)
            messages.Add(new { role = turn.Role, content = turn.Content });
        messages.Add(new { role = "user", content = request.UserMessage });

        // Replay this turn's tool calls and their results. The assistant message carrying the
        // calls has to be present before the tool results, or the provider rejects the thread.
        if (request.CompletedCalls is { Count: > 0 })
        {
            messages.Add(new
            {
                role = "assistant",
                content = (string?)null,
                tool_calls = request.CompletedCalls.Select(c => new
                {
                    id = c.Call.Id,
                    type = "function",
                    function = new { name = c.Call.Name, arguments = c.Call.ArgumentsJson }
                }).ToArray()
            });
            foreach (var (call, result) in request.CompletedCalls)
                messages.Add(new { role = "tool", tool_call_id = call.Id, content = result.Content });
        }

        try
        {
            object payload = request.Tools is { Count: > 0 }
                ? new
                {
                    model,
                    messages,
                    temperature = request.Temperature,
                    max_tokens = request.MaxTokens,
                    tools = request.Tools.Select(t => new
                    {
                        type = "function",
                        function = new
                        {
                            name = t.Name,
                            description = t.Description,
                            parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson)
                        }
                    }).ToArray()
                }
                : new
                {
                    model,
                    messages,
                    temperature = request.Temperature,
                    max_tokens = request.MaxTokens
                };

            using var doc = await PostAsync(ChatUrl(model), payload, ct);

            var root = doc.RootElement;
            var message = root.GetProperty("choices")[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";

            // The model asked for tools rather than answering. Hand the calls back so the caller
            // can apply its approval rules and run them.
            List<ToolCall>? toolCalls = null;
            if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array
                && tc.GetArrayLength() > 0)
            {
                toolCalls = new List<ToolCall>();
                foreach (var call in tc.EnumerateArray())
                {
                    var fn = call.GetProperty("function");
                    toolCalls.Add(new ToolCall(
                        call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("n"),
                        fn.GetProperty("name").GetString() ?? "",
                        fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}"));
                }
            }
            var usage = root.TryGetProperty("usage", out var u) ? u : default;
            var promptTokens = usage.ValueKind == JsonValueKind.Object
                ? usage.GetProperty("prompt_tokens").GetInt32() : 0;
            var completionTokens = usage.ValueKind == JsonValueKind.Object
                ? usage.GetProperty("completion_tokens").GetInt32() : 0;

            var noAnswer = toolCalls is null &&
                           (content.Contains("I don't know", StringComparison.OrdinalIgnoreCase) ||
                            content.Contains("no relevant information", StringComparison.OrdinalIgnoreCase));
            return new ChatCompletionResult(content, promptTokens, completionTokens, model, noAnswer,
                toolCalls);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat completion failed, answering from the local extractive engine.");
            var fallback = await new LocalChatCompletionProvider().CompleteAsync(request, ct);
            return fallback with { Content = fallback.Content + "\n\n_(LLM provider unavailable; answered locally.)_" };
        }
    }

    private static string BuildSystemPrompt(ChatCompletionRequest request)
    {
        var sb = new StringBuilder(request.SystemPrompt);
        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Answer using ONLY the numbered context below. Cite the sources you used with " +
                          "bracketed numbers such as [1] or [2] placed directly after the sentence they " +
                          "support. If the context does not contain the answer, say so plainly and do not " +
                          "guess.");
            sb.AppendLine();
            // Without this, a careful model refuses to count or total anything the source does not
            // state outright -- it will list eight employers and still answer "the context does not
            // say how many companies". Deriving a figure from the passages is reading, not guessing.
            sb.AppendLine("Working things out from the context is allowed and expected: count, total, " +
                          "compare, sort and summarise across the passages, and derive durations from " +
                          "dates. Doing so is not guessing. Only refuse when the underlying facts are " +
                          "genuinely absent. If a question is vague, answer the most reasonable reading " +
                          "of it and say which reading you took.");
            sb.AppendLine();
            sb.AppendLine("### Context");
            sb.AppendLine(request.Context);
        }
        else if (request.Tools is { Count: > 0 })
        {
            // With tools attached, an empty context is not a dead end: the answer may have to be
            // fetched rather than retrieved. Telling the model to give up here would stop it
            // calling the very tools it was given.
            sb.AppendLine();
            sb.AppendLine("No knowledge-base context was retrieved for this question. You have tools " +
                          "available: use one if it can answer the question. If no tool fits and you " +
                          "have no supporting documents, say so rather than answering from memory.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No knowledge-base context was retrieved for this question. Say that you could " +
                          "not find supporting documents rather than answering from memory.");
        }
        return sb.ToString();
    }
}

public static class VectorMath
{
    public static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm <= 0) return v;
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++) result[i] = v[i] / norm;
        return result;
    }

    public static double Cosine(float[] a, float[] b)
    {
        // Vectors from different embedding models are not comparable. Rather than scoring the
        // overlapping prefix and returning a plausible-looking number, treat it as no match.
        if (a.Length != b.Length) return 0;
        var n = a.Length;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na <= 0 || nb <= 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }
}
