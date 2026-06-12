using System.Runtime.CompilerServices;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Tests.Rag;

public class FakeLoader : IDocumentLoader
{
    public bool CanHandle(string filePath) => filePath.EndsWith(".fake", StringComparison.OrdinalIgnoreCase);

#pragma warning disable CS1998
    public async IAsyncEnumerable<DocumentSegment> LoadSegments(string filePath, [EnumeratorCancellation] CancellationToken ct = default)
#pragma warning restore CS1998
    {
        yield return new DocumentSegment { Text = "alpha beta gamma delta", SourceLocation = "p.1", BreakHint = SegmentBreakHint.Page };
        yield return new DocumentSegment { Text = "epsilon zeta eta theta", SourceLocation = "p.2", BreakHint = SegmentBreakHint.Page };
    }
}
