namespace SourceLens.Domain;

/// <summary>
/// Итог фазы ретрива для вопроса. UI показывает явный сигнал, когда заземление на источники не произошло.
/// </summary>
public enum RetrievalState
{
    /// <summary>Ретрив пропущен (вопрос короче MinQueryLength).</summary>
    Skipped,

    /// <summary>Ретрив выполнен, но ни один чанк не прошёл порог релевантности (или корпус пуст).</summary>
    NoneFound,

    /// <summary>Источники найдены и подмешаны в промпт.</summary>
    Found,
}
