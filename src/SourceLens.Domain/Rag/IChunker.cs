using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain.Rag;

public interface IChunker
{
    IAsyncEnumerable<Chunk> Chunk(IAsyncEnumerable<DocumentSegment> segments, CancellationToken ct = default);
}
