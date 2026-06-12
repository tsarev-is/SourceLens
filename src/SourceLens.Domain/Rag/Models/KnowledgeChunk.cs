namespace SourceLens.Domain.Rag.Models;

public class KnowledgeChunk
{
    public string Text { get; init; } = string.Empty;

    public string SourceTitle { get; init; } = string.Empty;

    public string SourceLocation { get; init; } = string.Empty;

    public float Score { get; init; }
}
