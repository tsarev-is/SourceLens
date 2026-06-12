using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Integrations.DocumentLoaders;

public class TextDocumentLoader : IDocumentLoader
{
    private static readonly string[] Extensions = { ".txt", ".md" };
    private static readonly Regex ParagraphSeparator = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);

    public bool CanHandle(string filePath)
    {
        return Extensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<DocumentSegment> LoadSegments(string filePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var index = 0;
        foreach (var paragraph in ParagraphSeparator.Split(content))
        {
            ct.ThrowIfCancellationRequested();
            var text = paragraph.Trim();
            if (text.Length == 0)
                continue;
            index++;

            yield return new DocumentSegment
            {
                Text = text,
                SourceLocation = $"§{index}",
                BreakHint = IsSectionHeading(text) ? SegmentBreakHint.Chapter : SegmentBreakHint.None,
            };
        }
    }

    private static bool IsSectionHeading(string paragraph)
    {
        return paragraph.StartsWith('#');
    }
}
