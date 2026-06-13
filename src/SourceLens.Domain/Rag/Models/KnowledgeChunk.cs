namespace SourceLens.Domain.Rag.Models;

public class KnowledgeChunk
{
    /// <summary>
    /// Id документа-источника чанка (0 — неизвестен, напр. в легаси-снимках истории до введения поля).
    /// Нужен для «Only this» и бейджа коллекции на карточке источника.
    /// </summary>
    public int DocumentId { get; init; }

    public string Text { get; init; } = string.Empty;

    public string SourceTitle { get; init; } = string.Empty;

    public string SourceLocation { get; init; } = string.Empty;

    public float Score { get; init; }
}
