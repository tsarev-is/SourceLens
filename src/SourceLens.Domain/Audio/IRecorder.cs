namespace SourceLens.Domain.Audio;

public interface IRecorder
{
    /// <summary>
    /// Записывать звук (raw PCM), пока активно условие; вернуть накопленные байты.
    /// </summary>
    Task<byte[]> Record(IWhileCondition whileCondition);

    AudioOptions GetAudioOptions();
}
