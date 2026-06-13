using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain;

public class RagAskResult
{
    public string Answer { get; init; } = string.Empty;

    public KnowledgeChunk[] Sources { get; init; } = Array.Empty<KnowledgeChunk>();

    /// <summary>
    /// Чем закончился ретрив: источники найдены, ничего не прошло порог, или ретрив пропущен.
    /// </summary>
    public RetrievalState Retrieval { get; init; } = RetrievalState.NoneFound;

    /// <summary>
    /// Имя коллекции, к которой был ограничен ретрив (null — «All sources»).
    /// </summary>
    public string? ScopeName { get; init; }

    /// <summary>
    /// True — область ограничена коллекцией без проиндексированных книг (ретрив не запускался).
    /// </summary>
    public bool ScopeEmpty { get; init; }
}
