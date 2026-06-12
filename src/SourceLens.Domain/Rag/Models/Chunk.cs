namespace SourceLens.Domain.Rag.Models;

public class Chunk
{
    public int Ordinal { get; init; }

    public string Text { get; init; } = string.Empty;

    public string SourceLocation { get; init; } = string.Empty;

    public int TokenCount { get; init; }
}
