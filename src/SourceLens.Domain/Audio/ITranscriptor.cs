namespace SourceLens.Domain.Audio;

public interface ITranscriptor
{
    /// <summary>
    /// Выполнить транскрипцию фразы (байты WAV) в текст.
    /// </summary>
    Task<string> Transcript(byte[] bytes);
}
