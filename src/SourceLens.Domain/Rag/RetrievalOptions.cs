namespace SourceLens.Domain.Rag;

public class RetrievalOptions
{
    /// <summary>
    /// Сколько чанков в итоге подмешать в промпт.
    /// </summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Минимальная длина вопроса для запуска retrieval; короче — только защита от случайного Enter.
    /// </summary>
    public int MinQueryLength { get; set; } = 3;

    /// <summary>
    /// Абсолютный порог косинусного сходства: чанки ниже отбрасываются. 0 — порог выключен.
    /// Для multilingual-e5 релевантные пассажи обычно дают ~0.80+, шум ~0.74–0.76.
    /// </summary>
    public float MinScore { get; set; }

    /// <summary>
    /// Относительный порог: чанк отбрасывается, если его оценка хуже лучшей более чем на это значение.
    /// 0 — выключен. Помогает срезать «хвост» при наличии одного-двух явных совпадений.
    /// </summary>
    public float MaxRelativeScoreDrop { get; set; }

    /// <summary>
    /// Размер пула кандидатов на каждый канал (dense / BM25) до слияния, MMR и дедупликации.
    /// </summary>
    public int CandidatePoolSize { get; set; } = 50;

    /// <summary>
    /// Баланс релевантности и разнообразия в MMR: 1.0 — чистая релевантность, 0.0 — чистое разнообразие.
    /// </summary>
    public float MmrLambda { get; set; } = 0.7f;

    /// <summary>
    /// Включить лексический канал (FTS5/BM25) и слияние с плотным поиском через RRF.
    /// При отсутствии FTS-таблицы ретривер прозрачно откатывается на чистый dense.
    /// </summary>
    public bool HybridSearch { get; set; } = true;

    /// <summary>
    /// Переписывать уточняющий вопрос в самодостаточный запрос отдельным вызовом LLM (точнее, но
    /// удваивает задержку/стоимость для каждого follow-up). Выключено — используется дешёвая эвристика
    /// (конкатенация предыдущего вопроса пользователя с текущим).
    /// </summary>
    public bool RewriteFollowUpQueries { get; set; } = true;
}
