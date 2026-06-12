using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Tests.Managers;

/// <summary>
/// Стаб ингеста: фиксирует вызовы, отдаёт IProgress наружу и умеет «зависать» на воротах.
/// </summary>
public class StubIngestor : IBookIngestor
{
    private readonly object _gate = new();
    private readonly List<string> _ingested = new();

    /// <summary>
    /// Если задан — IngestAsync не завершится, пока ворота не откроют.
    /// </summary>
    public TaskCompletionSource? Gate { get; set; }

    public volatile IProgress<IngestProgress>? LastProgress;

    public string[] Ingested
    {
        get
        {
            lock (_gate)
                return _ingested.ToArray();
        }
    }

    public async Task IngestAsync(string filePath, IProgress<IngestProgress>? progress = null, CancellationToken ct = default)
    {
        LastProgress = progress;
        lock (_gate)
            _ingested.Add(filePath);

        if (Gate != null)
            await Gate.Task;
    }
}
