using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace RagKnowledgeBaseApp.Api.Services.Ingestion;

/// <summary>A piece of extracted text plus where it came from, so citations can point at a page,
/// sheet or slide instead of just the file.</summary>
public record ExtractedSegment(string Text, string? Locator);

public interface ITextExtractor
{
    bool CanHandle(string fileName, string contentType);
    IReadOnlyList<ExtractedSegment> Extract(Stream stream, string fileName);
}

public static class SupportedFiles
{
    public static readonly string[] Extensions =
    {
        ".pdf", ".docx", ".xlsx", ".pptx", ".txt", ".md", ".csv", ".json", ".html", ".htm", ".log", ".xml"
    };

    public static bool IsSupported(string fileName)
        => Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
}

public class TextExtractionService
{
    private readonly IReadOnlyList<ITextExtractor> _extractors = new ITextExtractor[]
    {
        new PdfExtractor(), new WordExtractor(), new ExcelExtractor(),
        new PowerPointExtractor(), new PlainTextExtractor()
    };

    public IReadOnlyList<ExtractedSegment> Extract(Stream stream, string fileName, string contentType)
    {
        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(fileName, contentType))
            ?? throw new NotSupportedException(
                $"'{Path.GetExtension(fileName)}' is not a supported document type. " +
                $"Supported: {string.Join(", ", SupportedFiles.Extensions)}");
        return extractor.Extract(stream, fileName);
    }
}

public class PdfExtractor : ITextExtractor
{
    public bool CanHandle(string fileName, string contentType)
        => Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ExtractedSegment> Extract(Stream stream, string fileName)
    {
        using var ms = Buffered(stream);
        using var pdf = PdfDocument.Open(ms);
        var segments = new List<ExtractedSegment>();
        foreach (var page in pdf.GetPages())
        {
            var text = page.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            segments.Add(new ExtractedSegment(Clean(text), $"Page {page.Number}"));
        }
        if (segments.Count == 0)
            throw new InvalidOperationException(
                "No selectable text found. The PDF is likely a scan and needs OCR before indexing.");
        return segments;
    }

    internal static MemoryStream Buffered(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    internal static string Clean(string text) =>
        Regex.Replace(text.Replace("\r\n", "\n"), @"[ \t]{2,}", " ").Trim();
}

public class WordExtractor : ITextExtractor
{
    public bool CanHandle(string fileName, string contentType)
        => Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ExtractedSegment> Extract(Stream stream, string fileName)
    {
        using var ms = PdfExtractor.Buffered(stream);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return Array.Empty<ExtractedSegment>();

        var segments = new List<ExtractedSegment>();
        var sb = new StringBuilder();
        var section = "Section 1";
        var sectionNo = 1;

        foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var text = para.InnerText;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var style = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
            var isHeading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase);
            if (isHeading && sb.Length > 0)
            {
                segments.Add(new ExtractedSegment(PdfExtractor.Clean(sb.ToString()), section));
                sb.Clear();
                sectionNo++;
                section = $"Section {sectionNo}: {Shorten(text)}";
            }
            else if (isHeading)
            {
                section = $"Section {sectionNo}: {Shorten(text)}";
            }
            sb.AppendLine(text);
        }
        if (sb.Length > 0) segments.Add(new ExtractedSegment(PdfExtractor.Clean(sb.ToString()), section));
        return segments;
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..60] + "...";
}

public class ExcelExtractor : ITextExtractor
{
    public bool CanHandle(string fileName, string contentType)
        => Path.GetExtension(fileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ExtractedSegment> Extract(Stream stream, string fileName)
    {
        using var ms = PdfExtractor.Buffered(stream);
        using var doc = SpreadsheetDocument.Open(ms, false);
        var wbPart = doc.WorkbookPart;
        if (wbPart is null) return Array.Empty<ExtractedSegment>();

        var sharedStrings = wbPart.SharedStringTablePart?.SharedStringTable?
            .Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>()
            .Select(x => x.InnerText).ToArray() ?? Array.Empty<string>();

        var segments = new List<ExtractedSegment>();
        foreach (var sheet in wbPart.Workbook?.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                     ?? Enumerable.Empty<DocumentFormat.OpenXml.Spreadsheet.Sheet>())
        {
            if (sheet.Id?.Value is null) continue;
            var part = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            var sb = new StringBuilder();
            foreach (var row in part.Worksheet?.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>()
                         ?? Enumerable.Empty<DocumentFormat.OpenXml.Spreadsheet.Row>())
            {
                var cells = row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                    .Select(c => CellText(c, sharedStrings))
                    .Where(v => !string.IsNullOrWhiteSpace(v));
                var line = string.Join(" | ", cells);
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
            }
            if (sb.Length > 0)
                segments.Add(new ExtractedSegment(sb.ToString().Trim(), $"Sheet {sheet.Name}"));
        }
        return segments;
    }

    private static string CellText(DocumentFormat.OpenXml.Spreadsheet.Cell cell, string[] sharedStrings)
    {
        var value = cell.CellValue?.InnerText ?? cell.InnerText;
        if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString
            && int.TryParse(value, out var idx) && idx >= 0 && idx < sharedStrings.Length)
            return sharedStrings[idx];
        return value;
    }
}

public class PowerPointExtractor : ITextExtractor
{
    public bool CanHandle(string fileName, string contentType)
        => Path.GetExtension(fileName).Equals(".pptx", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ExtractedSegment> Extract(Stream stream, string fileName)
    {
        using var ms = PdfExtractor.Buffered(stream);
        using var doc = PresentationDocument.Open(ms, false);
        var slideParts = doc.PresentationPart?.SlideParts.ToList() ?? new List<SlidePart>();
        var segments = new List<ExtractedSegment>();
        for (var i = 0; i < slideParts.Count; i++)
        {
            var texts = (slideParts[i].Slide?.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                         ?? Enumerable.Empty<DocumentFormat.OpenXml.Drawing.Text>())
                .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t));
            var joined = string.Join("\n", texts).Trim();
            if (joined.Length > 0) segments.Add(new ExtractedSegment(joined, $"Slide {i + 1}"));
        }
        return segments;
    }
}

public class PlainTextExtractor : ITextExtractor
{
    public bool CanHandle(string fileName, string contentType)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".txt" or ".md" or ".csv" or ".json" or ".log" or ".xml" or ".html" or ".htm";
    }

    public IReadOnlyList<ExtractedSegment> Extract(Stream stream, string fileName)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".html" or ".htm") text = StripHtml(text);
        return new[] { new ExtractedSegment(PdfExtractor.Clean(text), null) };
    }

    private static string StripHtml(string html)
    {
        var noScript = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var noTags = Regex.Replace(noScript, "<[^>]+>", " ");
        return Regex.Replace(System.Net.WebUtility.HtmlDecode(noTags), @"\s{2,}", " ").Trim();
    }
}
