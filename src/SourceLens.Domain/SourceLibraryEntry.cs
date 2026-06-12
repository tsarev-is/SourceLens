namespace SourceLens.Domain;

/// <summary>
/// Карточка библиотеки источников: проиндексированный документ из БД или файл в очереди индексации.
/// </summary>
public class SourceLibraryEntry
{
    /// <summary>
    /// Id документа в БД; null, пока файл ещё индексируется.
    /// </summary>
    public int? DocumentId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public int ChunkCount { get; init; }

    public bool Indexing { get; init; }

    /// <summary>
    /// 0..100, осмыслен только при <see cref="Indexing"/>.
    /// </summary>
    public int ProgressPercent { get; init; }
}
