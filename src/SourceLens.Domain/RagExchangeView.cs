using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain;

/// <summary>
/// Сохранённый обмен для UI: вопрос, ответ и снапшот источников из SourcesJson.
/// </summary>
public class RagExchangeView
{
    public int Id { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string Question { get; init; } = string.Empty;

    public string Answer { get; init; } = string.Empty;

    public KnowledgeChunk[] Sources { get; init; } = Array.Empty<KnowledgeChunk>();
}
