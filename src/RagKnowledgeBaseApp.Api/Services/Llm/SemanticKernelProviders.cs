using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;

namespace RagKnowledgeBaseApp.Api.Services.Llm;

/// <summary>Chat completion through Semantic Kernel rather than raw HTTP.
///
/// It sits behind the same IChatCompletionProvider the rest of the application already depends on,
/// so retrieval, security trimming, the token budget and the tool-approval gate are untouched: only
/// the call to the model changes. Llm:Engine switches between this and the hand-written client, so
/// the two can be compared and either can be rolled back to without a deployment.
///
/// Function calling is deliberately not auto-invoked. Semantic Kernel will happily run a function
/// and feed the result back on its own, but that would step straight past the human-approval gate,
/// which is the one thing in this platform that must not be bypassed. Asking for the calls and
/// returning them keeps the decision where it belongs.</summary>
public class SemanticKernelChatProvider : IChatCompletionProvider
{
    private readonly LlmOptions _options;
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;
    private readonly ILogger<SemanticKernelChatProvider> _logger;
    private readonly bool _azure;

    public SemanticKernelChatProvider(LlmOptions options, ILogger<SemanticKernelChatProvider> logger)
    {
        _options = options;
        _logger = logger;
        _azure = options.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase);

        var builder = Kernel.CreateBuilder();
        if (_azure)
        {
            builder.AddAzureOpenAIChatCompletion(
                deploymentName: string.IsNullOrWhiteSpace(options.ChatDeployment)
                    ? options.ChatModel : options.ChatDeployment,
                endpoint: options.Endpoint,
                apiKey: options.ApiKey);
        }
        else
        {
            // The endpoint is only passed when it points somewhere other than OpenAI, so an
            // OpenAI-compatible gateway works while the default path uses the connector's own base
            // address. The two overloads are distinct: one takes a Uri that must not be null.
            var custom = !string.IsNullOrWhiteSpace(options.Endpoint) &&
                         !options.Endpoint.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
            if (custom)
                builder.AddOpenAIChatCompletion(
                    modelId: options.ChatModel,
                    endpoint: new Uri(options.Endpoint),
                    apiKey: options.ApiKey);
            else
                builder.AddOpenAIChatCompletion(
                    modelId: options.ChatModel,
                    apiKey: options.ApiKey);
        }

        _kernel = builder.Build();
        _chat = _kernel.GetRequiredService<IChatCompletionService>();
    }

    public string ProviderName => _azure
        ? $"Semantic Kernel · Azure OpenAI ({_options.ChatModel})"
        : $"Semantic Kernel · OpenAI ({_options.ChatModel})";

    public bool IsLive => _options.HasCredentials;

    public async Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request,
        CancellationToken ct = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.ChatModel : request.Model;

        var history = new ChatHistory();
        history.AddSystemMessage(BuildSystemPrompt(request));
        foreach (var turn in request.History)
        {
            if (turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                history.AddUserMessage(turn.Content);
            else
                history.AddAssistantMessage(turn.Content);
        }
        history.AddUserMessage(request.UserMessage);

        // Replay this turn's tool calls and their results so the model can continue from them.
        // Rendered as plain messages rather than provider-specific call objects: the contract with
        // RagService is the same either way, and this keeps one shape across both engines.
        if (request.CompletedCalls is { Count: > 0 })
        {
            var sb = new StringBuilder("Results of the tools you asked to run:");
            foreach (var (call, result) in request.CompletedCalls)
            {
                sb.AppendLine();
                sb.AppendLine($"### {call.Name}");
                sb.AppendLine(result.Content);
            }
            history.AddUserMessage(sb.ToString());
        }

        var settings = new OpenAIPromptExecutionSettings
        {
            ModelId = model,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens
        };

        Kernel? kernel = null;
        if (request.Tools is { Count: > 0 })
        {
            kernel = BuildToolKernel(request.Tools);
            // autoInvoke:false is the load-bearing argument. See the class comment.
            settings.FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false);
        }

        try
        {
            var reply = await _chat.GetChatMessageContentAsync(history, settings, kernel, ct);
            var content = reply.Content ?? "";

            var calls = FunctionCallContent.GetFunctionCalls(reply)
                .Select(c => new ToolCall(
                    c.Id ?? Guid.NewGuid().ToString("n"),
                    // The plugin name is Semantic Kernel's own grouping and is deliberately dropped:
                    // ToolService already issues a flat, tenant-unique name, and that is what the
                    // approval and routing logic matches on.
                    c.FunctionName,
                    JsonSerializer.Serialize(c.Arguments ?? new KernelArguments())))
                .ToList();

            var (promptTokens, completionTokens) = ReadUsage(reply);

            var noAnswer = calls.Count == 0 &&
                           (content.Contains("I don't know", StringComparison.OrdinalIgnoreCase) ||
                            content.Contains("no relevant information", StringComparison.OrdinalIgnoreCase));

            return new ChatCompletionResult(content, promptTokens, completionTokens, model, noAnswer,
                calls.Count > 0 ? calls : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Semantic Kernel completion failed, answering from the local extractive engine.");
            var fallback = await new LocalChatCompletionProvider().CompleteAsync(request, ct);
            return fallback with { Content = fallback.Content + "\n\n_(LLM provider unavailable; answered locally.)_" };
        }
    }

    /// <summary>Builds a throwaway kernel holding one function per tool operation.
    ///
    /// The functions are never executed here, so their bodies are empty: they exist only to carry
    /// the name, description and argument schema the model needs in order to ask for a call.</summary>
    private Kernel BuildToolKernel(IReadOnlyList<ToolDefinition> tools)
    {
        var functions = new List<KernelFunction>();

        foreach (var tool in tools)
        {
            try
            {
                functions.Add(KernelFunctionFactory.CreateFromMethod(
                    () => string.Empty,
                    new KernelFunctionFromMethodOptions
                    {
                        FunctionName = tool.Name,
                        Description = tool.Description,
                        Parameters = DescribeParameters(tool.ParametersJson)
                    }));
            }
            catch (Exception ex)
            {
                // One malformed schema must not cost the model every other tool it was given.
                _logger.LogWarning(ex, "Skipping tool {Tool}: its parameter schema could not be read", tool.Name);
            }
        }

        var kernel = Kernel.CreateBuilder().Build();
        if (functions.Count > 0)
            kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions("tools", functions));
        return kernel;
    }

    /// <summary>Turns a JSON Schema object into the per-parameter metadata Semantic Kernel expects.
    /// Our tools carry one whole-object schema, which is what OpenAI wants; SK builds that same
    /// object back up from individual parameters, so the properties are unpacked here.</summary>
    private static List<KernelParameterMetadata> DescribeParameters(string parametersJson)
    {
        var parameters = new List<KernelParameterMetadata>();
        if (string.IsNullOrWhiteSpace(parametersJson)) return parameters;

        using var doc = JsonDocument.Parse(parametersJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return parameters;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (r.GetString() is { } name) required.Add(name);

        foreach (var property in properties.EnumerateObject())
        {
            parameters.Add(new KernelParameterMetadata(property.Name)
            {
                Description = property.Value.TryGetProperty("description", out var d)
                    ? d.GetString() ?? "" : "",
                IsRequired = required.Contains(property.Name),
                Schema = KernelJsonSchema.Parse(property.Value.GetRawText())
            });
        }
        return parameters;
    }

    /// <summary>Usage is provider-shaped metadata rather than part of the SK contract, so it is read
    /// defensively: a missing count costs an analytics figure, not an answer.</summary>
    private static (int Prompt, int Completion) ReadUsage(ChatMessageContent reply)
    {
        if (reply.Metadata is null || !reply.Metadata.TryGetValue("Usage", out var usage) || usage is null)
            return (0, 0);

        var type = usage.GetType();
        var prompt = type.GetProperty("InputTokenCount") ?? type.GetProperty("PromptTokens");
        var completion = type.GetProperty("OutputTokenCount") ?? type.GetProperty("CompletionTokens");

        return (ToInt(prompt?.GetValue(usage)), ToInt(completion?.GetValue(usage)));

        static int ToInt(object? value) => value is null ? 0
            : int.TryParse(value.ToString(), out var n) ? n : 0;
    }

    /// <summary>Identical wording to the HTTP engine on purpose: the two must be comparable, and a
    /// difference in answers should mean a difference in the model call, not in the prompt.</summary>
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

// Semantic Kernel's chat completion is generally available, but its embedding generators are still
// marked for evaluation and will change. The suppression is scoped to this one class rather than
// set project-wide, so the next experimental API someone reaches for still has to be a decision.
#pragma warning disable SKEXP0010

/// <summary>Embeddings through Semantic Kernel, behind the same interface the ingestion pipeline
/// and the retriever already use.
///
/// Semantic Kernel exposes embeddings through Microsoft.Extensions.AI rather than its own service
/// type, which is the direction the whole .NET AI stack is moving; using it here means the vector
/// width and the batching stay ours while the provider call does not.</summary>
public class SemanticKernelEmbeddingProvider : IEmbeddingProvider
{
    private readonly LlmOptions _options;
    private readonly Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> _generator;
    private readonly bool _azure;

    public SemanticKernelEmbeddingProvider(LlmOptions options)
    {
        _options = options;
        _azure = options.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase);

        var builder = Kernel.CreateBuilder();
        if (_azure)
        {
            builder.AddAzureOpenAIEmbeddingGenerator(
                deploymentName: string.IsNullOrWhiteSpace(options.EmbeddingDeployment)
                    ? options.EmbeddingModel : options.EmbeddingDeployment,
                endpoint: options.Endpoint,
                apiKey: options.ApiKey,
                dimensions: options.EmbeddingDimensions);
        }
        else
        {
            builder.AddOpenAIEmbeddingGenerator(
                modelId: options.EmbeddingModel,
                apiKey: options.ApiKey,
                dimensions: options.EmbeddingDimensions);
        }

        _generator = builder.Build()
            .GetRequiredService<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>();
    }

    public string ProviderName => _azure
        ? $"Semantic Kernel · Azure OpenAI ({_options.EmbeddingModel})"
        : $"Semantic Kernel · OpenAI ({_options.EmbeddingModel})";

    public bool IsLive => _options.HasCredentials;
    public int Dimensions => _options.EmbeddingDimensions;

    public async Task<float[]> EmbedAsync(string text, string model, CancellationToken ct = default)
    {
        var result = await _generator.GenerateAsync([text], cancellationToken: ct);
        return result[0].Vector.ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, string model,
        CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var result = await _generator.GenerateAsync(texts, cancellationToken: ct);
        return result.Select(e => e.Vector.ToArray()).ToList();
    }
}

#pragma warning restore SKEXP0010
