namespace SourceLens.Domain.Rag.Models;

public class IngestProgress
{
    public string FilePath { get; init; } = string.Empty;

    public int ChunksProcessed { get; init; }

    public int TotalChunks { get; init; }

    public string? Stage { get; init; }
}
