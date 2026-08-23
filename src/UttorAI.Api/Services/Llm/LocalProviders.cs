using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UttorAI.Api.Services.Llm;

/// <summary>Deterministic hashed bag-of-words embedding. It is not competitive with a real
/// embedding model, but it is dependency free, stable across restarts and good enough to make the
/// end-to-end pipeline demonstrable without an API key. Configure Llm:Provider=OpenAI to swap it.</summary>
public class LocalEmbeddingProvider : IEmbeddingProvider
{
    public const int Dim = 384;

    public string ProviderName => "Local (hashed n-gram)";
    public bool IsLive => false;
    public int Dimensions => Dim;

    public Task<float[]> EmbedAsync(string text, string model, CancellationToken ct = default)
        => Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, string model,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    public static float[] Embed(string text)
    {
        var vector = new float[Dim];
        var tokens = Tokenizer.Tokenize(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            Add(vector, tokens[i], 1f);
            // A light bigram signal keeps short phrases distinguishable.
            if (i + 1 < tokens.Count) Add(vector, tokens[i] + "_" + tokens[i + 1], 0.5f);
        }
        Normalize(vector);
        return vector;
    }

    private static void Add(float[] vector, string token, float weight)
    {
        var hash = BitConverter.ToUInt32(MD5.HashData(Encoding.UTF8.GetBytes(token)), 0);
        var slot = (int)(hash % Dim);
        var sign = (hash & 0x8000_0000) == 0 ? 1f : -1f;
        vector[slot] += sign * weight;
    }

    private static void Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm <= 0) return;
        for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
    }
}

public static class Tokenizer
{
    private static readonly Regex WordRegex = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the","a","an","and","or","of","to","in","is","are","was","were","be","been","for","on","with",
        "as","by","that","this","it","at","from","but","not","have","has","had","do","does","did","if",
        "then","than","so","such","you","your","we","our","they","their","i","me","my","can","could",
        "will","would","should","what","which","who","whom","how","when","where","why","about","into"
    };

    public static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        foreach (Match m in WordRegex.Matches(text ?? ""))
        {
            var w = m.Value.ToLowerInvariant();
            if (w.Length < 2 || StopWords.Contains(w)) continue;
            result.Add(w);
        }
        return result;
    }

    /// <summary>Cheap token estimate (~4 characters per token) used for usage accounting.</summary>
    public static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : text.Length / 4 + 1;
}

/// <summary>Extractive answer generator used when no LLM credentials are configured. It never
/// invents text: it selects the sentences from the retrieved context that best match the question
/// and returns them with citation markers, or reports that it has no answer.</summary>
public class LocalChatCompletionProvider : IChatCompletionProvider
{
    public string ProviderName => "Local (extractive, no LLM key configured)";
    public bool IsLive => false;

    public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var promptTokens = Tokenizer.EstimateTokens(request.SystemPrompt + request.Context + request.UserMessage);

        if (string.IsNullOrWhiteSpace(request.Context))
        {
            const string msg = "I could not find anything in the connected knowledge bases that answers " +
                               "this question. Try rephrasing it, or ask an administrator to add the " +
                               "relevant document.";
            return Task.FromResult(new ChatCompletionResult(msg, promptTokens,
                Tokenizer.EstimateTokens(msg), "local-extractive", NoAnswer: true));
        }

        var queryTerms = Tokenizer.Tokenize(request.UserMessage).ToHashSet();
        var scored = new List<(double score, int source, string sentence)>();

        foreach (var block in SplitContextBlocks(request.Context))
        {
            foreach (var sentence in SplitSentences(block.text))
            {
                var terms = Tokenizer.Tokenize(sentence);
                if (terms.Count == 0) continue;
                var overlap = terms.Count(queryTerms.Contains);
                if (overlap == 0) continue;
                var score = overlap / Math.Sqrt(terms.Count) + (block.index == 1 ? 0.15 : 0);
                scored.Add((score, block.index, sentence.Trim()));
            }
        }

        string content;
        var noAnswer = false;
        if (scored.Count == 0)
        {
            var first = SplitContextBlocks(request.Context).FirstOrDefault();
            content = first.text is null
                ? "I could not find an answer in the retrieved documents."
                : $"I could not find a direct answer, but the closest passage in the knowledge base says:\n\n" +
                  $"\"{Truncate(first.text, 500)}\" [{first.index}]";
            noAnswer = true;
        }
        else
        {
            var picks = scored.OrderByDescending(s => s.score).Take(4).ToList();
            var ordered = picks.OrderBy(p => p.source).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("Based on the retrieved documents:");
            sb.AppendLine();
            foreach (var p in ordered)
                sb.AppendLine($"- {Truncate(p.sentence, 400)} [{p.source}]");
            sb.AppendLine();
            sb.Append("(Answer assembled by the built-in extractive engine. Configure an OpenAI or " +
                      "Azure OpenAI key to generate natural-language answers.)");
            content = sb.ToString();
        }

        return Task.FromResult(new ChatCompletionResult(content, promptTokens,
            Tokenizer.EstimateTokens(content), "local-extractive", noAnswer));
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max].TrimEnd() + "...";

    /// <summary>Context blocks are emitted by RagService as "[n] file - locator\ntext".</summary>
    private static IEnumerable<(int index, string text)> SplitContextBlocks(string context)
    {
        var matches = Regex.Matches(context, @"^\[(\d+)\][^\n]*\n(.*?)(?=^\[\d+\]|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        foreach (Match m in matches)
            yield return (int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim());
    }

    private static IEnumerable<string> SplitSentences(string text)
        => Regex.Split(text, @"(?<=[.!?])\s+|\n+")
            .Where(s => s.Trim().Length > 25);
}
