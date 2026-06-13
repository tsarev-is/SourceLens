namespace SourceLens.Domain;

/// <summary>
/// Вид области поиска сессии: вся библиотека, коллекция или явный набор конкретных документов.
/// </summary>
public enum ScopeKind
{
    All,
    Collection,
    Documents,
}

/// <summary>
/// Область поиска сессии: «All sources», конкретная коллекция или явный набор источников.
/// Залипает за сессией (персист в app_settings), как и выбор коллекции.
/// </summary>
public sealed record SessionScope(ScopeKind Kind, int? CollectionId, IReadOnlyList<int> DocumentIds)
{
    public static readonly SessionScope All = new(ScopeKind.All, null, Array.Empty<int>());

    public static SessionScope Collection(int id) => new(ScopeKind.Collection, id, Array.Empty<int>());

    public static SessionScope Documents(IReadOnlyList<int> ids) =>
        new(ScopeKind.Documents, null, ids);
}
