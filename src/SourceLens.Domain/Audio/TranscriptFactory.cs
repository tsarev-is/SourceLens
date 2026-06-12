using System.Collections.Concurrent;

namespace SourceLens.Domain.Audio;

/// <summary>
/// Пул транскрипторов: Acquire блокирует при исчерпании, Release возвращает экземпляр в пул.
/// </summary>
public class TranscriptFactory
{
    private readonly ConcurrentBag<ITranscriptor> _pool = new();
    private readonly SemaphoreSlim _semaphore;

    public TranscriptFactory(params ITranscriptor[] transcriptors)
    {
        AllCount = transcriptors.Length;
        _semaphore = new SemaphoreSlim(transcriptors.Length, transcriptors.Length);
        foreach (var transcriptor in transcriptors)
            _pool.Add(transcriptor);
    }

    public int AllCount { get; }

    public ITranscriptor Acquire()
    {
        _semaphore.Wait();

        ITranscriptor? result;
        while (!_pool.TryTake(out result))
            Task.Delay(100).Wait();

        return result;
    }

    public void Release(ITranscriptor instance)
    {
        if (instance == null)
            throw new ArgumentNullException(nameof(instance));

        _pool.Add(instance);
        _semaphore.Release();
    }
}
