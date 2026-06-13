using System.Security.Cryptography;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain;

/// <summary>
/// Библиотека источников: единая последовательная очередь индексации (стартовый скан папки книг
/// и файлы, добавленные из UI), удаление документов из индекса и сводный список для окна Source library.
/// </summary>
public class SourceLibraryManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly string[] DefaultExtensions = { ".pdf", ".epub", ".txt", ".md" };

    /// <summary>
    /// Имя дефолтной коллекции — бакет для книг без пользовательской коллекции (неудаляемая).
    /// </summary>
    public const string DefaultCollectionName = "General";

    /// <summary>
    /// Нейтральный цвет дефолтной коллекции (отличает её от цветных пользовательских).
    /// </summary>
    public const string DefaultCollectionColor = "#7a818d";

    /// <summary>
    /// Палитра цветов пользовательских коллекций (из мокапа docs/RagWindow.dc.html).
    /// </summary>
    private static readonly string[] CollectionPalette =
        { "#6aa6ff", "#4ec98a", "#b38df5", "#e3b341", "#e0897f", "#56c8c0", "#d6a04f", "#8fa0ff" };

    private readonly Func<SourceLensContext> _getContext;
    private readonly IBookIngestor _ingestor;
    private readonly IReadOnlyList<string> _supportedExtensions;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _copyGate = new(1, 1);
    private readonly Dictionary<string, PendingItem> _pending = new(StringComparer.Ordinal);
    private Task _tail = Task.CompletedTask;
    private long _nextSeq;

    public SourceLibraryManager(Func<SourceLensContext> getContext, IBookIngestor ingestor,
        string booksFolder, IReadOnlyList<string>? supportedExtensions = null)
    {
        _getContext = getContext;
        _ingestor = ingestor;
        BooksFolder = booksFolder;
        _supportedExtensions = supportedExtensions ?? DefaultExtensions;

        // Дефолтная коллекция должна существовать до любых операций (прод сеет её и в DatabaseInitializer,
        // но тесты создают контекст напрямую через EnsureCreated — поэтому гарантируем здесь тоже).
        using var ctx = _getContext();
        ctx.EnsureDefaultCollection(DefaultCollectionName, DefaultCollectionColor);
    }

    public string BooksFolder { get; }

    /// <summary>
    /// Структурное изменение библиотеки (постановка в очередь / завершение / удаление / коллекции).
    /// Подписчик перестраивает список целиком. Может прийти с фонового потока.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Тик прогресса индексации одного файла (полный путь, процент 0..100). Отдельно от <see cref="Changed"/>,
    /// чтобы UI обновлял прогресс-бар карточки на месте и не перестраивал весь список (иначе он моргает).
    /// Может прийти с фонового потока.
    /// </summary>
    public event Action<string, int>? IndexingProgress;

    /// <summary>
    /// Создаёт папку книг при отсутствии и ставит в очередь все поддерживаемые файлы (рекурсивно, в ordinal-порядке).
    /// </summary>
    public void QueueFolderScan()
    {
        if (!Directory.Exists(BooksFolder))
        {
            Directory.CreateDirectory(BooksFolder);
            Logger.Info("Created books folder: {0}", Path.GetFullPath(BooksFolder));
        }

        var files = Directory.EnumerateFiles(BooksFolder, "*", SearchOption.AllDirectories)
            .Where(f => _supportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            Logger.Info("No supported files found in {0}; skipping background ingest", BooksFolder);
            return;
        }

        Logger.Info("Background ingest starting for {0} file(s)", files.Length);
        foreach (var file in files)
            Queue(file);
    }

    /// <summary>
    /// Копирует выбранный файл в папку книг (дедуп по SHA256, при коллизии имён — уникальный суффикс)
    /// и ставит копию в очередь. Завершается при постановке в очередь; индексация продолжается в фоне.
    /// Если задан <paramref name="collectionId"/> (пользовательская коллекция), документ по завершении
    /// индексации добавляется в неё, а карточка очереди сразу показывается в её фильтре.
    /// </summary>
    public Task AddSourceAsync(string sourcePath, int? collectionId = null)
    {
        return Task.Run(async () =>
        {
            var destination = await CopyIntoBooksFolder(sourcePath);
            Queue(destination, collectionId);
        });
    }

    /// <summary>
    /// Удаляет документ и его чанки только из БД; файл в папке книг остаётся.
    /// False, если документ не найден или его файл сейчас в очереди индексации.
    /// </summary>
    public async Task<bool> RemoveDocumentAsync(int documentId)
    {
        await using var ctx = _getContext();
        var document = ctx.GetBookDocuments().FirstOrDefault(d => d.Id == documentId);
        if (document == null)
            return false;

        lock (_gate)
        {
            if (_pending.ContainsKey(Path.GetFullPath(document.FilePath)))
            {
                Logger.Warn("Cannot remove {0}: file is being indexed", document.FilePath);
                return false;
            }
        }

        var deleted = await ctx.DeleteBookDocument(documentId);
        if (deleted)
        {
            Logger.Info("Removed source {0} from index", document.FilePath);
            Changed?.Invoke();
        }

        return deleted;
    }

    // ---------- Коллекции источников ----------

    /// <summary>
    /// Коллекции для UI (дефолтная первой) со счётчиком книг.
    /// </summary>
    public Entities.BookCollectionSummary[] GetCollections()
    {
        using var ctx = _getContext();
        return ctx.GetCollectionSummaries();
    }

    /// <summary>
    /// Создаёт пользовательскую коллекцию, подбирая следующий цвет палитры. Возвращает её id.
    /// </summary>
    public async Task<int> CreateCollection(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return 0;

        await using var ctx = _getContext();
        // Цвет — по числу уже существующих пользовательских (не дефолтных) коллекций.
        var userCount = ctx.GetCollectionSummaries().Count(c => !c.IsDefault);
        var color = CollectionPalette[userCount % CollectionPalette.Length];
        var collection = await ctx.AddCollection(trimmed, color);
        Logger.Info("Created collection '{0}' (#{1})", trimmed, collection.Id);
        Changed?.Invoke();
        return collection.Id;
    }

    /// <summary>
    /// Переименовывает коллекцию (дефолтную тоже можно). False — если не найдена.
    /// </summary>
    public async Task<bool> RenameCollection(int collectionId, string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        await using var ctx = _getContext();
        var renamed = await ctx.RenameCollection(collectionId, trimmed);
        if (renamed)
            Changed?.Invoke();
        return renamed;
    }

    /// <summary>
    /// Удаляет пользовательскую коллекцию (дефолтную нельзя); её книги уходят в General.
    /// </summary>
    public async Task<bool> DeleteCollection(int collectionId)
    {
        await using var ctx = _getContext();
        var deleted = await ctx.DeleteCollection(collectionId);
        if (deleted)
        {
            Logger.Info("Deleted collection #{0}", collectionId);
            Changed?.Invoke();
        }

        return deleted;
    }

    /// <summary>
    /// Добавляет документ в коллекцию (no-op для дефолтной/несуществующей).
    /// </summary>
    public async Task AddToCollection(int documentId, int collectionId)
    {
        await using var ctx = _getContext();
        await ctx.AddCollectionMember(collectionId, documentId);
        Changed?.Invoke();
    }

    /// <summary>
    /// Убирает документ из коллекции (если членств не осталось — он снова в General).
    /// </summary>
    public async Task RemoveFromCollection(int documentId, int collectionId)
    {
        await using var ctx = _getContext();
        await ctx.RemoveCollectionMember(collectionId, documentId);
        Changed?.Invoke();
    }

    /// <summary>
    /// Сводный список: сначала файлы в очереди (новые сверху), затем документы из БД (новые сверху).
    /// Документ, чей файл сейчас переиндексируется, показывается только карточкой очереди.
    /// </summary>
    public SourceLibraryEntry[] GetEntries()
    {
        PendingItem[] pending;
        lock (_gate)
            pending = _pending.Values.OrderByDescending(p => p.Seq).ToArray();

        var inFlightPaths = pending.Select(p => p.FilePath).ToHashSet(StringComparer.Ordinal);

        using var ctx = _getContext();
        var collectionMap = ctx.GetDocumentCollectionMap();
        var documents = ctx.GetBookDocuments()
            .Where(d => !inFlightPaths.Contains(Path.GetFullPath(d.FilePath)))
            .Select(d => new SourceLibraryEntry
            {
                DocumentId = d.Id,
                Title = d.Title,
                FilePath = d.FilePath,
                ChunkCount = d.ChunkCount,
                CollectionIds = collectionMap.TryGetValue(d.Id, out var ids) ? ids : Array.Empty<int>(),
            });

        return pending
            .Select(p => new SourceLibraryEntry
            {
                Title = p.Title,
                FilePath = p.FilePath,
                Indexing = true,
                ProgressPercent = p.ProgressPercent,
                // Загрузка в коллекцию: карточка очереди видна в её фильтре уже во время индексации.
                CollectionIds = p.CollectionId is { } cid ? new[] { cid } : Array.Empty<int>(),
            })
            .Concat(documents)
            .ToArray();
    }

    /// <summary>
    /// Завершается, когда всё поставленное в очередь к этому моменту обработано (тесты/диагностика).
    /// </summary>
    public Task WhenQueueDrained()
    {
        lock (_gate)
            return _tail;
    }

    private void Queue(string filePath, int? collectionId = null)
    {
        var fullPath = Path.GetFullPath(filePath);
        lock (_gate)
        {
            if (_pending.ContainsKey(fullPath))
                return;

            var item = new PendingItem(fullPath, _nextSeq++, collectionId);
            _pending[fullPath] = item;
            _tail = ProcessAfterAsync(_tail, item);
        }

        Changed?.Invoke();
    }

    private async Task ProcessAfterAsync(Task previous, PendingItem item)
    {
        await previous.ConfigureAwait(false);
        try
        {
            var progress = new PendingProgress(this, item);
            await Task.Run(() => _ingestor.IngestAsync(item.FilePath, progress)).ConfigureAwait(false);

            // Загрузка велась в выбранную пользователем коллекцию — прописываем членство свежесозданному
            // документу (иначе он осядет в дефолтной General). Только при успешной индексации.
            if (item.CollectionId is { } collectionId)
                await AddIndexedToCollection(item.FilePath, collectionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Битый файл не должен убивать очередь: логируем и продолжаем со следующего.
            Logger.Error(ex, "Failed to ingest {0}", item.FilePath);
        }
        finally
        {
            lock (_gate)
                _pending.Remove(item.FilePath);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Находит только что проиндексированный документ по пути его файла и добавляет в коллекцию.
    /// No-op, если документ не нашёлся или коллекция дефолтная/удалена (см. AddCollectionMember).
    /// </summary>
    private async Task AddIndexedToCollection(string fullPath, int collectionId)
    {
        try
        {
            await using var ctx = _getContext();
            var document = ctx.GetBookDocuments()
                .FirstOrDefault(d => Path.GetFullPath(d.FilePath) == fullPath);
            if (document == null)
            {
                Logger.Warn("Indexed document for {0} not found; cannot add to collection #{1}", fullPath, collectionId);
                return;
            }

            await ctx.AddCollectionMember(collectionId, document.Id);
            Logger.Info("Added {0} to collection #{1} on upload", Path.GetFileName(fullPath), collectionId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to add {0} to collection #{1}", fullPath, collectionId);
        }
    }

    private async Task<string> CopyIntoBooksFolder(string sourcePath)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(BooksFolder))
            Directory.CreateDirectory(BooksFolder);

        var booksRoot = Path.GetFullPath(BooksFolder);
        if (fullSource.StartsWith(booksRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return fullSource;

        var name = Path.GetFileNameWithoutExtension(fullSource);
        var extension = Path.GetExtension(fullSource);
        string? sourceSha = null;

        // Конкурентные добавления одноимённых файлов не должны гоняться за одним именем копии.
        await _copyGate.WaitAsync();
        try
        {
            for (var i = 0; ; i++)
            {
                var candidate = Path.Combine(booksRoot, i == 0 ? name + extension : $"{name} ({i}){extension}");
                if (!File.Exists(candidate))
                {
                    File.Copy(fullSource, candidate);
                    Logger.Info("Copied {0} into books folder as {1}", fullSource, candidate);
                    return candidate;
                }

                sourceSha ??= await ComputeSha256(fullSource);
                if (await ComputeSha256(candidate) == sourceSha)
                {
                    Logger.Info("File {0} already present in books folder as {1}", fullSource, candidate);
                    return candidate;
                }
            }
        }
        finally
        {
            _copyGate.Release();
        }
    }

    private static async Task<string> ComputeSha256(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash);
    }

    private sealed class PendingItem
    {
        public PendingItem(string filePath, long seq, int? collectionId)
        {
            FilePath = filePath;
            Title = Path.GetFileNameWithoutExtension(filePath);
            Seq = seq;
            CollectionId = collectionId;
        }

        public string FilePath { get; }

        public string Title { get; }

        public long Seq { get; }

        /// <summary>
        /// Пользовательская коллекция, в которую грузили файл; null — без явного членства (General).
        /// </summary>
        public int? CollectionId { get; }

        public int Processed { get; set; }

        public int Total { get; set; }

        public int ProgressPercent => Total > 0 ? Math.Clamp(Processed * 100 / Total, 0, 100) : 0;
    }

    /// <summary>
    /// Прогресс с потока ингеста: без Progress&lt;T&gt;, чтобы не зависеть от захваченного SyncContext.
    /// </summary>
    private sealed class PendingProgress : IProgress<IngestProgress>
    {
        private readonly SourceLibraryManager _owner;
        private readonly PendingItem _item;

        public PendingProgress(SourceLibraryManager owner, PendingItem item)
        {
            _owner = owner;
            _item = item;
        }

        public void Report(IngestProgress value)
        {
            Logger.Info("[{0}] {1}: {2}/{3}", value.Stage, Path.GetFileName(value.FilePath), value.ChunksProcessed, value.TotalChunks);
            int percent;
            lock (_owner._gate)
            {
                _item.Processed = value.ChunksProcessed;
                _item.Total = value.TotalChunks;
                percent = _item.ProgressPercent;
            }

            // Прогресс — отдельным событием: UI двигает прогресс-бар карточки на месте, не перестраивая
            // весь список (иначе интерфейс моргает на каждом чанке).
            _owner.IndexingProgress?.Invoke(_item.FilePath, percent);
        }
    }
}
