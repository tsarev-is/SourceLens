using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;
using VersOne.Epub;

namespace SourceLens.Integrations.DocumentLoaders;

public class EpubDocumentLoader : IDocumentLoader
{
    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public bool CanHandle(string filePath)
    {
        return Path.GetExtension(filePath).Equals(".epub", StringComparison.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<DocumentSegment> LoadSegments(string filePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var book = await EpubReader.ReadBookAsync(filePath);
        var index = 0;
        foreach (var item in book.ReadingOrder)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var text = StripHtml(item.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            yield return new DocumentSegment
            {
                Text = text,
                SourceLocation = $"ch.{index}: {item.Key}",
                BreakHint = SegmentBreakHint.Chapter,
            };
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;
        var noTags = TagPattern.Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return WhitespacePattern.Replace(decoded, " ").Trim();
    }
}
