using NAudio.Wave;
using SourceLens.Domain.Audio;

namespace SourceLens.Integrations.Recorders;

/// <summary>
/// Запись с микрофона через NAudio WaveInEvent (Windows).
/// Выбор реализации по платформе делается в composition root.
/// </summary>
public class NAudioInputRecorder : IRecorder
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly AudioOptions _audioOptions;

    public NAudioInputRecorder(AudioOptions audioOptions)
    {
        _audioOptions = audioOptions;
    }

    public async Task<byte[]> Record(IWhileCondition whileCondition)
    {
        using var capture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(_audioOptions.Rate, _audioOptions.BitsPerSample, _audioOptions.Channels),
        };

        using var memory = new MemoryStream();

        capture.DataAvailable += (_, e) => memory.Write(e.Buffer, 0, e.BytesRecorded);

        var mixer = capture.GetMixerLine();
        Logger.Debug($"Recording started. Device: {mixer.Name};");

        capture.StartRecording();

        await Task.Run(async () =>
        {
            while (whileCondition.IsActive)
                await Task.Delay(300);
        });

        capture.StopRecording();

        Logger.Debug($"Recording finished. Device: {mixer.Name}; Bytes: {memory.Length};");

        return memory.ToArray();
    }

    public AudioOptions GetAudioOptions() => _audioOptions;
}
