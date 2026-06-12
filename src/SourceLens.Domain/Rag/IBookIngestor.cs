using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain.Rag;

public interface IBookIngestor
{
    Task IngestAsync(string filePath, IProgress<IngestProgress>? progress = null, CancellationToken ct = default);
}
