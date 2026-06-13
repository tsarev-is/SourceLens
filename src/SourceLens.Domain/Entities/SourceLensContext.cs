using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SourceLens.Domain.Entities.Models;

namespace SourceLens.Domain.Entities;

public record BookChunkLookup(int ChunkId, int DocumentId, int Ordinal, string Text, string SourceLocation, string Title, byte[] Embedding);

/// <summary>
/// Лёгкая строка для кэша векторов ретривера (без текста).
/// </summary>
public record BookChunkVector(int ChunkId, int DocumentId, int Ordinal, byte[] Embedding);

/// <summary>
/// Текст победителей ретрива, подгружаемый по id уже после ранжирования.
/// </summary>
public record BookChunkText(int ChunkId, string Text, string SourceLocation, string Title);

/// <summary>
/// Книга для выбора области поиска в UI.
/// </summary>
public record BookDocumentSummary(int DocumentId, string Title);

/// <summary>
/// Коллекция источников для UI: id, имя, цвет, флаг дефолтной и число входящих книг.
/// </summary>
public record BookCollectionSummary(int Id, string Name, string Color, bool IsDefault, int Count);

public class SourceLensContext : DbContext
{
    /// <summary>
    /// Имя FTS5-таблицы лексического канала ретрива (см. <see cref="DatabaseInitializer"/>).
    /// </summary>
    public const string FtsTableName = "book_chunks_fts";

    private DbSet<BookDocumentItem> _bookDocumentItems;
    private DbSet<BookChunkItem> _bookChunkItems;
    private DbSet<BookCollectionItem> _bookCollectionItems;
    private DbSet<BookCollectionMemberItem> _bookCollectionMemberItems;
    private DbSet<RagSessionItem> _ragSessionItems;
    private DbSet<RagExchangeItem> _ragExchangeItems;
    private DbSet<AppSettingItem> _appSettingItems;

    public SourceLensContext(DbContextOptions<SourceLensContext> options) : base(options)
    {
        _bookDocumentItems = Set<BookDocumentItem>();
        _bookChunkItems = Set<BookChunkItem>();
        _bookCollectionItems = Set<BookCollectionItem>();
        _bookCollectionMemberItems = Set<BookCollectionMemberItem>();
        _ragSessionItems = Set<RagSessionItem>();
        _ragExchangeItems = Set<RagExchangeItem>();
        _appSettingItems = Set<AppSettingItem>();
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        BookDocumentItem.OrmAccess.Configure(mb.Entity<BookDocumentItem>());
        BookChunkItem.OrmAccess.Configure(mb.Entity<BookChunkItem>());
        BookCollectionItem.OrmAccess.Configure(mb.Entity<BookCollectionItem>());
        BookCollectionMemberItem.OrmAccess.Configure(mb.Entity<BookCollectionMemberItem>());
        RagSessionItem.OrmAccess.Configure(mb.Entity<RagSessionItem>());
        RagExchangeItem.OrmAccess.Configure(mb.Entity<RagExchangeItem>());
        AppSettingItem.OrmAccess.Configure(mb.Entity<AppSettingItem>());
    }

    public Task<BookChunkLookup[]> GetBookChunks(string embedderModelId, int embedderDimensions, CancellationToken ct = default)
    {
        return (from chunk in _bookChunkItems.AsNoTracking()
                join doc in _bookDocumentItems.AsNoTracking() on chunk.DocumentId equals doc.Id
                where doc.EmbedderModelId == embedderModelId && doc.EmbedderDimensions == embedderDimensions
                select new BookChunkLookup(chunk.Id, chunk.DocumentId, chunk.Ordinal, chunk.Text, chunk.SourceLocation, doc.Title, chunk.Embedding))
            .ToArrayAsync(ct);
    }

    /// <summary>
    /// Все векторы корпуса для (model, dims) без текста — для in-memory кэша ретривера.
    /// </summary>
    public Task<BookChunkVector[]> GetBookChunkVectors(string embedderModelId, int embedderDimensions, CancellationToken ct = default)
    {
        return (from chunk in _bookChunkItems.AsNoTracking()
                join doc in _bookDocumentItems.AsNoTracking() on chunk.DocumentId equals doc.Id
                where doc.EmbedderModelId == embedderModelId && doc.EmbedderDimensions == embedderDimensions
                select new BookChunkVector(chunk.Id, chunk.DocumentId, chunk.Ordinal, chunk.Embedding))
            .ToArrayAsync(ct);
    }

    /// <summary>
    /// Текст/заголовок/локация для победителей ретрива по их id.
    /// </summary>
    public async Task<Dictionary<int, BookChunkText>> GetBookChunkTexts(IReadOnlyCollection<int> chunkIds, CancellationToken ct = default)
    {
        if (chunkIds.Count == 0)
            return new Dictionary<int, BookChunkText>();

        var idSet = chunkIds as ICollection<int> ?? chunkIds.ToArray();
        var rows = await (from chunk in _bookChunkItems.AsNoTracking()
                join doc in _bookDocumentItems.AsNoTracking() on chunk.DocumentId equals doc.Id
                where idSet.Contains(chunk.Id)
                select new BookChunkText(chunk.Id, chunk.Text, chunk.SourceLocation, doc.Title))
            .ToArrayAsync(ct);
        return rows.ToDictionary(r => r.ChunkId);
    }

    /// <summary>
    /// Снимок (count, maxId) корпуса для (model, dims): дёшево проверить, не устарел ли кэш.
    /// </summary>
    public async Task<(int Count, int MaxId)> GetChunkStat(string embedderModelId, int embedderDimensions, CancellationToken ct = default)
    {
        var q = from chunk in _bookChunkItems.AsNoTracking()
                join doc in _bookDocumentItems.AsNoTracking() on chunk.DocumentId equals doc.Id
                where doc.EmbedderModelId == embedderModelId && doc.EmbedderDimensions == embedderDimensions
                select chunk.Id;
        var count = await q.CountAsync(ct);
        var maxId = count == 0 ? 0 : await q.MaxAsync(ct);
        return (count, maxId);
    }

    public BookDocumentItem[] GetBookDocuments()
    {
        // SQLite cannot ORDER BY DateTimeOffset; the table is one row per book, so sort client-side.
        return _bookDocumentItems.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(p => p.AddedAt)
            .ThenByDescending(p => p.Id)
            .ToArray();
    }

    public BookDocumentSummary[] GetBookDocumentSummaries(string embedderModelId, int embedderDimensions)
    {
        return _bookDocumentItems.AsNoTracking()
            .Where(d => d.EmbedderModelId == embedderModelId && d.EmbedderDimensions == embedderDimensions)
            .AsEnumerable()
            .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .Select(d => new BookDocumentSummary(d.Id, d.Title))
            .ToArray();
    }

    public async Task<bool> DeleteBookDocument(int documentId, CancellationToken ct = default)
    {
        var document = await _bookDocumentItems.FirstOrDefaultAsync(p => p.Id == documentId, ct);
        if (document == null)
            return false;

        await RemoveDocumentFts(documentId, ct);
        _bookChunkItems.RemoveRange(_bookChunkItems.Where(c => c.DocumentId == documentId));
        _bookCollectionMemberItems.RemoveRange(_bookCollectionMemberItems.Where(m => m.DocumentId == documentId));
        _bookDocumentItems.Remove(document);
        await SaveChangesAsync(ct);
        return true;
    }

    // ---------- Коллекции источников ----------

    /// <summary>
    /// Гарантирует наличие дефолтной коллекции (General — бакет «без коллекции», неудаляемая).
    /// Идемпотентно; возвращает её. Вызывается из DatabaseInitializer (прод) и SourceLibraryManager (тесты).
    /// </summary>
    public BookCollectionItem EnsureDefaultCollection(string name, string color)
    {
        var existing = _bookCollectionItems.FirstOrDefault(c => c.IsDefault && !c.Deleted);
        if (existing != null)
            return existing;

        var created = _bookCollectionItems.Add(BookCollectionItem.Create(name, color, isDefault: true)).Entity;
        SaveChanges();
        return created;
    }

    /// <summary>
    /// Коллекции для UI (дефолтная первой, затем по имени) со счётчиком входящих книг.
    /// </summary>
    public BookCollectionSummary[] GetCollectionSummaries()
    {
        var collections = _bookCollectionItems.AsNoTracking().Where(c => !c.Deleted).AsEnumerable().ToArray();
        var counts = GetCollectionCounts();
        return collections
            .OrderByDescending(c => c.IsDefault)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new BookCollectionSummary(c.Id, c.Name, c.Color, c.IsDefault,
                counts.TryGetValue(c.Id, out var n) ? n : 0))
            .ToArray();
    }

    public BookCollectionItem? FindCollection(int collectionId)
    {
        return _bookCollectionItems.FirstOrDefault(c => c.Id == collectionId && !c.Deleted);
    }

    public BookCollectionItem? GetDefaultCollection()
    {
        return _bookCollectionItems.AsNoTracking().FirstOrDefault(c => c.IsDefault && !c.Deleted);
    }

    /// <summary>
    /// Число книг в каждой коллекции. Для дефолтной (General) — книги без членства в живых коллекциях
    /// («uncategorized»); для пользовательской — число её строк-связок.
    /// </summary>
    public Dictionary<int, int> GetCollectionCounts()
    {
        var live = _bookCollectionItems.AsNoTracking().Where(c => !c.Deleted).ToArray();
        var liveIds = live.Select(c => c.Id).ToHashSet();
        var memberships = _bookCollectionMemberItems.AsNoTracking()
            .Where(m => liveIds.Contains(m.CollectionId))
            .Select(m => new { m.CollectionId, m.DocumentId })
            .AsEnumerable()
            .ToArray();

        // Все живые коллекции присутствуют в словаре (0 — если без книг).
        var counts = live.ToDictionary(c => c.Id, _ => 0);
        foreach (var group in memberships.GroupBy(m => m.CollectionId))
            counts[group.Key] = group.Count();

        var defaultCollection = live.FirstOrDefault(c => c.IsDefault);
        if (defaultCollection != null)
        {
            var totalDocs = _bookDocumentItems.AsNoTracking().Count();
            var assignedDocs = memberships.Select(m => m.DocumentId).Distinct().Count();
            counts[defaultCollection.Id] = Math.Max(0, totalDocs - assignedDocs);
        }

        return counts;
    }

    /// <summary>
    /// Идентификаторы книг коллекции для ограничения области ретрива. Для дефолтной — книги без
    /// членства в живых коллекциях; для пользовательской — её книги (только существующие).
    /// </summary>
    public int[] GetCollectionDocumentIds(int collectionId)
    {
        var collection = _bookCollectionItems.AsNoTracking().FirstOrDefault(c => c.Id == collectionId && !c.Deleted);
        if (collection == null)
            return Array.Empty<int>();

        if (collection.IsDefault)
        {
            var liveIds = _bookCollectionItems.AsNoTracking().Where(c => !c.Deleted).Select(c => c.Id).ToHashSet();
            var assigned = _bookCollectionMemberItems.AsNoTracking()
                .Where(m => liveIds.Contains(m.CollectionId))
                .Select(m => m.DocumentId)
                .ToHashSet();
            return _bookDocumentItems.AsNoTracking().Select(d => d.Id).AsEnumerable()
                .Where(id => !assigned.Contains(id))
                .ToArray();
        }

        var existingDocs = _bookDocumentItems.AsNoTracking().Select(d => d.Id).ToHashSet();
        return _bookCollectionMemberItems.AsNoTracking()
            .Where(m => m.CollectionId == collectionId)
            .Select(m => m.DocumentId)
            .AsEnumerable()
            .Where(existingDocs.Contains)
            .ToArray();
    }

    /// <summary>
    /// Карта documentId → id живых пользовательских коллекций (для списка источников в UI).
    /// </summary>
    public Dictionary<int, int[]> GetDocumentCollectionMap()
    {
        var liveIds = _bookCollectionItems.AsNoTracking().Where(c => !c.Deleted && !c.IsDefault).Select(c => c.Id).ToHashSet();
        return _bookCollectionMemberItems.AsNoTracking()
            .Where(m => liveIds.Contains(m.CollectionId))
            .Select(m => new { m.DocumentId, m.CollectionId })
            .AsEnumerable()
            .GroupBy(m => m.DocumentId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.CollectionId).ToArray());
    }

    /// <summary>
    /// Идентификаторы пользовательских (живых) коллекций, в которые входит документ.
    /// </summary>
    public int[] GetDocumentCollectionIds(int documentId)
    {
        var liveIds = _bookCollectionItems.AsNoTracking().Where(c => !c.Deleted && !c.IsDefault).Select(c => c.Id).ToHashSet();
        return _bookCollectionMemberItems.AsNoTracking()
            .Where(m => m.DocumentId == documentId && liveIds.Contains(m.CollectionId))
            .Select(m => m.CollectionId)
            .ToArray();
    }

    public async Task<BookCollectionItem> AddCollection(string name, string color, CancellationToken ct = default)
    {
        var collection = _bookCollectionItems.Add(BookCollectionItem.Create(name, color)).Entity;
        await SaveChangesAsync(ct);
        return collection;
    }

    public async Task<bool> RenameCollection(int collectionId, string name, CancellationToken ct = default)
    {
        var collection = await _bookCollectionItems.FirstOrDefaultAsync(c => c.Id == collectionId && !c.Deleted, ct);
        if (collection == null)
            return false;

        collection.Rename(name);
        await SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Мягко удаляет пользовательскую коллекцию и жёстко — её связки (осиротевшие книги уходят в General).
    /// Дефолтную коллекцию удалить нельзя.
    /// </summary>
    public async Task<bool> DeleteCollection(int collectionId, CancellationToken ct = default)
    {
        var collection = await _bookCollectionItems.FirstOrDefaultAsync(c => c.Id == collectionId && !c.Deleted, ct);
        if (collection == null || collection.IsDefault)
            return false;

        _bookCollectionMemberItems.RemoveRange(_bookCollectionMemberItems.Where(m => m.CollectionId == collectionId));
        collection.MarkDeleted();
        await SaveChangesAsync(ct);
        return true;
    }

    public async Task AddCollectionMember(int collectionId, int documentId, CancellationToken ct = default)
    {
        var collection = await _bookCollectionItems.FirstOrDefaultAsync(c => c.Id == collectionId && !c.Deleted && !c.IsDefault, ct);
        if (collection == null)
            return;

        var exists = await _bookCollectionMemberItems
            .AnyAsync(m => m.CollectionId == collectionId && m.DocumentId == documentId, ct);
        if (exists)
            return;

        _bookCollectionMemberItems.Add(BookCollectionMemberItem.Create(collectionId, documentId));
        await SaveChangesAsync(ct);
    }

    public async Task RemoveCollectionMember(int collectionId, int documentId, CancellationToken ct = default)
    {
        var rows = _bookCollectionMemberItems.Where(m => m.CollectionId == collectionId && m.DocumentId == documentId);
        _bookCollectionMemberItems.RemoveRange(rows);
        await SaveChangesAsync(ct);
    }

    // ---------- Лексический канал (FTS5) ----------

    /// <summary>
    /// Существует ли FTS5-таблица (фреш-БД через DatabaseInitializer; легаси/InMemory — нет).
    /// </summary>
    public async Task<bool> HasLexicalIndex(CancellationToken ct = default)
    {
        if (!Database.IsRelational())
            return false;

        var conn = Database.GetDbConnection();
        await EnsureOpen(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
        AddParam(cmd, "$n", FtsTableName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null;
    }

    /// <summary>
    /// BM25-топ id чанков по тексту для (model, dims) и опционально области книг.
    /// </summary>
    public async Task<int[]> SearchLexicalChunkIds(string query, string embedderModelId, int embedderDimensions,
        IReadOnlyCollection<int>? documentIds, int limit, CancellationToken ct = default)
    {
        var match = BuildMatchQuery(query);
        if (match == null || limit <= 0 || !Database.IsRelational())
            return Array.Empty<int>();

        var conn = Database.GetDbConnection();
        await EnsureOpen(conn, ct);

        // FTS5: MATCH и bm25() работают по имени таблицы, а не по алиасу — поэтому ссылаемся на FtsTableName напрямую.
        var sql = new StringBuilder()
            .Append("SELECT c.Id FROM ").Append(FtsTableName).Append(' ')
            .Append("JOIN book_chunks c ON c.Id = ").Append(FtsTableName).Append(".rowid ")
            .Append("JOIN book_documents d ON d.Id = c.DocumentId ")
            .Append("WHERE ").Append(FtsTableName).Append(" MATCH $q AND d.EmbedderModelId = $m AND d.EmbedderDimensions = $dim");

        await using var cmd = conn.CreateCommand();
        AddParam(cmd, "$q", match);
        AddParam(cmd, "$m", embedderModelId);
        AddParam(cmd, "$dim", embedderDimensions);
        if (documentIds is { Count: > 0 })
        {
            sql.Append(" AND c.DocumentId IN (");
            var i = 0;
            foreach (var id in documentIds)
            {
                if (i > 0)
                    sql.Append(',');
                var name = "$d" + i;
                sql.Append(name);
                AddParam(cmd, name, id);
                i++;
            }
            sql.Append(')');
        }
        sql.Append(" ORDER BY bm25(").Append(FtsTableName).Append(") LIMIT $lim");
        AddParam(cmd, "$lim", limit);
        cmd.CommandText = sql.ToString();

        try
        {
            var ids = new List<int>(limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetInt32(0));
            return ids.ToArray();
        }
        catch (DbException)
        {
            // FTS-таблица отсутствует/повреждена — лексический канал просто выключается.
            return Array.Empty<int>();
        }
    }

    /// <summary>
    /// Перестроить FTS-строки документа (после ингеста). No-op без FTS-таблицы.
    /// </summary>
    public async Task RebuildDocumentFts(int documentId, CancellationToken ct = default)
    {
        if (!await HasLexicalIndex(ct))
            return;

        var conn = Database.GetDbConnection();
        await EnsureOpen(conn, ct);
        await using var del = conn.CreateCommand();
        del.CommandText = $"DELETE FROM {FtsTableName} WHERE rowid IN (SELECT Id FROM book_chunks WHERE DocumentId = $d)";
        AddParam(del, "$d", documentId);
        await del.ExecuteNonQueryAsync(ct);

        await using var ins = conn.CreateCommand();
        ins.CommandText = $"INSERT INTO {FtsTableName}(rowid, Text) SELECT Id, Text FROM book_chunks WHERE DocumentId = $d";
        AddParam(ins, "$d", documentId);
        await ins.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Удалить FTS-строки документа (перед удалением/переиндексацией его чанков). No-op без FTS-таблицы.
    /// </summary>
    public async Task RemoveDocumentFts(int documentId, CancellationToken ct = default)
    {
        if (!await HasLexicalIndex(ct))
            return;

        var conn = Database.GetDbConnection();
        await EnsureOpen(conn, ct);
        await using var del = conn.CreateCommand();
        del.CommandText = $"DELETE FROM {FtsTableName} WHERE rowid IN (SELECT Id FROM book_chunks WHERE DocumentId = $d)";
        AddParam(del, "$d", documentId);
        await del.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Превращает свободный текст в безопасный FTS5 MATCH: слова в кавычках через OR (recall на именах/терминах).
    /// Возвращает null, если значимых токенов нет.
    /// </summary>
    private static string? BuildMatchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            tokens.Add(sb.ToString());

        if (tokens.Count == 0)
            return null;

        return string.Join(" OR ", tokens.Select(t => "\"" + t + "\""));
    }

    private static async Task EnsureOpen(DbConnection conn, CancellationToken ct)
    {
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    public RagSessionItem[] GetRagSessions()
    {
        return _ragSessionItems.Where(p => !p.Deleted).OrderByDescending(p => p.Id).ToArray();
    }

    public RagExchangeItem[] GetRagExchanges(int sessionId)
    {
        return _ragExchangeItems.Where(p => p.RagSessionId == sessionId && !p.Deleted).OrderBy(p => p.Id).ToArray();
    }

    public RagSessionItem? GetLastRagSession()
    {
        return _ragSessionItems.Where(p => !p.Deleted).OrderByDescending(p => p.Id).FirstOrDefault();
    }

    public RagSessionItem? FindRagSession(int sessionId)
    {
        return _ragSessionItems.FirstOrDefault(p => p.Id == sessionId && !p.Deleted);
    }

    public RagExchangeItem? FindRagExchange(int exchangeId)
    {
        return _ragExchangeItems.FirstOrDefault(p => p.Id == exchangeId && !p.Deleted);
    }

    public async Task<RagSessionItem> AddRagSession(string? title = null)
    {
        var session = (await AddAsync(RagSessionItem.Create(title))).Entity;
        return session;
    }

    public async Task<RagExchangeItem> AddRagExchange(RagSessionItem session, string question, string sourcesJson,
        string? scopeName = null, string? scopeColor = null)
    {
        var exchange = (await AddAsync(RagExchangeItem.Create(session, question, sourcesJson, scopeName, scopeColor))).Entity;
        return exchange;
    }

    public string? GetSetting(string key)
    {
        return _appSettingItems.AsNoTracking().FirstOrDefault(p => p.Key == key)?.Value;
    }

    public void SetSetting(string key, string value)
    {
        var existing = _appSettingItems.FirstOrDefault(p => p.Key == key);
        if (existing == null)
        {
            _appSettingItems.Add(AppSettingItem.Create(key, value));
        }
        else
        {
            existing.SetValue(value);
        }
    }

    /// <summary>
    /// Удаляет настройку по ключу (no-op, если её нет). Без SaveChanges.
    /// </summary>
    public void DeleteSetting(string key)
    {
        var existing = _appSettingItems.FirstOrDefault(p => p.Key == key);
        if (existing != null)
            _appSettingItems.Remove(existing);
    }
}
