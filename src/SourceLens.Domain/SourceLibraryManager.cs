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
    }

    public string BooksFolder { get; }

    /// <summary>
    /// Изменение состояния библиотеки (постановка в очередь / прогресс / завершение / удаление). Может прийти с фонового потока.
    /// </summary>
    public event Action? Changed;

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
    /// </summary>
    public Task AddSourceAsync(string sourcePath)
    {
        return Task.Run(async () =>
        {
            var destination = await CopyIntoBooksFolder(sourcePath);
            Queue(destination);
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
        var documents = ctx.GetBookDocuments()
            .Where(d => !inFlightPaths.Contains(Path.GetFullPath(d.FilePath)))
            .Select(d => new SourceLibraryEntry
            {
                DocumentId = d.Id,
                Title = d.Title,
                FilePath = d.FilePath,
                ChunkCount = d.ChunkCount,
            });

        return pending
            .Select(p => new SourceLibraryEntry
            {
                Title = p.Title,
                FilePath = p.FilePath,
                Indexing = true,
                ProgressPercent = p.ProgressPercent,
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

    private void Queue(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        lock (_gate)
        {
            if (_pending.ContainsKey(fullPath))
                return;

            var item = new PendingItem(fullPath, _nextSeq++);
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
        public PendingItem(string filePath, long seq)
        {
            FilePath = filePath;
            Title = Path.GetFileNameWithoutExtension(filePath);
            Seq = seq;
        }

        public string FilePath { get; }

        public string Title { get; }

        public long Seq { get; }

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
            lock (_owner._gate)
            {
                _item.Processed = value.ChunksProcessed;
                _item.Total = value.TotalChunks;
            }

            _owner.Changed?.Invoke();
        }
    }
}
