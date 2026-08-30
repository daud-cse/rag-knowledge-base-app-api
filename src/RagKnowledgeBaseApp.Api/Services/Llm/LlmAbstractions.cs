namespace RagKnowledgeBaseApp.Api.Services.Llm;

public record ChatTurn(string Role, string Content);

public record ChatCompletionRequest(
    string Model,
    string SystemPrompt,
    IReadOnlyList<ChatTurn> History,
    string UserMessage,
    double Temperature,
    int MaxTokens,
    // Formatted, numbered retrieval context. Empty when RAG is off or nothing matched.
    string Context);

public record ChatCompletionResult(string Content, int PromptTokens, int CompletionTokens,
    string Model, bool NoAnswer);

public interface IEmbeddingProvider
{
    string ProviderName { get; }
    bool IsLive { get; }
    int Dimensions { get; }
    Task<float[]> EmbedAsync(string text, string model, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, string model, CancellationToken ct = default);
}

public interface IChatCompletionProvider
{
    string ProviderName { get; }
    bool IsLive { get; }
    Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default);
}

public class LlmOptions
{
    /// <summary>OpenAI, AzureOpenAI or Local.</summary>
    public string Provider { get; set; } = "Local";
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ChatModel { get; set; } = "gpt-4o-mini";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    /// <summary>Width of the embedding vectors. Sent to the provider when it supports shortening
    /// (the text-embedding-3 family does) and used as the vector size of the Qdrant collection.</summary>
    public int EmbeddingDimensions { get; set; } = 1536;
    /// <summary>Azure OpenAI only.</summary>
    public string ApiVersion { get; set; } = "2024-08-01-preview";
    public string ChatDeployment { get; set; } = "";
    public string EmbeddingDeployment { get; set; } = "";
    /// <summary>Rough per-1K-token prices used by the analytics cost estimate.</summary>
    public double PromptCostPer1K { get; set; } = 0.00015;
    public double CompletionCostPer1K { get; set; } = 0.0006;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(ApiKey) &&
                                  !Provider.Equals("Local", StringComparison.OrdinalIgnoreCase);
}
