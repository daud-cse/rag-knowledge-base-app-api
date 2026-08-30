using System.Text;
using System.Text.RegularExpressions;

namespace RagKnowledgeBaseApp.Api.Services.Ingestion;

public record TextChunk(string Text, string? Locator, int Ordinal);

/// <summary>Sentence-aware sliding-window chunker. Sizes are in characters, which keeps the
/// configuration understandable in the admin UI and independent of the tokenizer in use.</summary>
public class Chunker
{
    public IReadOnlyList<TextChunk> Chunk(IReadOnlyList<ExtractedSegment> segments, int chunkSize, int overlap)
    {
        chunkSize = Math.Clamp(chunkSize, 200, 8000);
        overlap = Math.Clamp(overlap, 0, chunkSize / 2);

        var chunks = new List<TextChunk>();
        var ordinal = 0;

        foreach (var segment in segments)
        {
            var sentences = SplitSentences(segment.Text);
            var current = new StringBuilder();

            foreach (var sentence in sentences)
            {
                if (current.Length > 0 && current.Length + sentence.Length + 1 > chunkSize)
                {
                    var text = current.ToString().Trim();
                    if (text.Length > 0) chunks.Add(new TextChunk(text, segment.Locator, ordinal++));
                    current.Clear();
                    if (overlap > 0 && text.Length > overlap)
                        current.Append(text[^overlap..]).Append(' ');
                }

                // A single sentence longer than the window is hard-split rather than dropped.
                if (sentence.Length > chunkSize)
                {
                    for (var i = 0; i < sentence.Length; i += chunkSize)
                    {
                        var slice = sentence.Substring(i, Math.Min(chunkSize, sentence.Length - i));
                        chunks.Add(new TextChunk(slice.Trim(), segment.Locator, ordinal++));
                    }
                    continue;
                }

                current.Append(sentence).Append(' ');
            }

            var tail = current.ToString().Trim();
            if (tail.Length > 20) chunks.Add(new TextChunk(tail, segment.Locator, ordinal++));
        }

        return chunks;
    }

    private static IEnumerable<string> SplitSentences(string text)
        => Regex.Split(text ?? "", @"(?<=[.!?;:])\s+|\n{2,}")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
}
