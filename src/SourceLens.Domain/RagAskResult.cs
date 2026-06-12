using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain;

public class RagAskResult
{
    public string Answer { get; init; } = string.Empty;

    public KnowledgeChunk[] Sources { get; init; } = Array.Empty<KnowledgeChunk>();
}
