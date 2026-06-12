using SourceLens.Domain.Audio;

namespace SourceLens.Tests.Voice;

/// <summary>
/// Стаб рекордера: ждёт, пока условие активно, и отдаёт заранее заданные байты.
/// </summary>
public class StubRecorder : IRecorder
{
    private readonly byte[] _data;
    private readonly AudioOptions _audioOptions;

    public StubRecorder(byte[]? data = null, AudioOptions? audioOptions = null)
    {
        _data = data ?? [1, 2, 3, 4];
        _audioOptions = audioOptions ?? new AudioOptions { Rate = 16000, BitsPerSample = 16, Channels = 1 };
    }

    public int RecordCalls { get; private set; }

    public async Task<byte[]> Record(IWhileCondition whileCondition)
    {
        RecordCalls++;
        while (whileCondition.IsActive)
            await Task.Delay(10);

        return _data;
    }

    public AudioOptions GetAudioOptions() => _audioOptions;
}
