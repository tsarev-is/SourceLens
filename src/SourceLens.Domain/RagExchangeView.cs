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

    /// <summary>
    /// Имя коллекции, к которой был ограничен ретрив обмена (null — «All sources»).
    /// </summary>
    public string? ScopeName { get; init; }

    /// <summary>
    /// Цвет коллекции области поиска обмена (null — «All sources»).
    /// </summary>
    public string? ScopeColor { get; init; }
}
