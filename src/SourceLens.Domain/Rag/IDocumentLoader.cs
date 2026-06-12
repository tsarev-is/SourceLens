using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain.Rag;

public interface IDocumentLoader
{
    bool CanHandle(string filePath);

    IAsyncEnumerable<DocumentSegment> LoadSegments(string filePath, CancellationToken ct = default);
}
