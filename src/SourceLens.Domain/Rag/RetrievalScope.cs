namespace SourceLens.Domain.Rag;

/// <summary>
/// Ограничение области ретрива. Пустой/<c>null</c> <see cref="DocumentIds"/> — поиск по всей библиотеке;
/// иначе — только по указанным книгам. Хранится per-session, чтобы вопрос о конкретной книге
/// не «загрязнялся» похожими пассажами из других книг.
/// </summary>
public sealed class RetrievalScope
{
    public IReadOnlyCollection<int>? DocumentIds { get; init; }

    public bool IsWholeLibrary => DocumentIds == null || DocumentIds.Count == 0;

    public static readonly RetrievalScope WholeLibrary = new();

    public static RetrievalScope ForDocuments(params int[] documentIds) =>
        new() { DocumentIds = documentIds };
}
