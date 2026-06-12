using System.Runtime.CompilerServices;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;
using UglyToad.PdfPig;

namespace SourceLens.Integrations.DocumentLoaders;

public class PdfDocumentLoader : IDocumentLoader
{
    public bool CanHandle(string filePath)
    {
        return Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<DocumentSegment> LoadSegments(string filePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        using var document = PdfDocument.Open(filePath, new ParsingOptions { SkipMissingFonts = true });
        var pageNumber = 0;
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            pageNumber++;
            var text = page.Text;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            yield return new DocumentSegment
            {
                Text = text,
                SourceLocation = $"p.{pageNumber}",
                BreakHint = SegmentBreakHint.Page,
            };
        }
    }
}
