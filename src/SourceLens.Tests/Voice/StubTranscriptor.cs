using SourceLens.Domain.Audio;

namespace SourceLens.Tests.Voice;

/// <summary>
/// Стаб транскриптора: возвращает фиксированный текст, запоминает полученные байты.
/// </summary>
public class StubTranscriptor : ITranscriptor
{
    private readonly string _text;

    public StubTranscriptor(string text = "stub transcript")
    {
        _text = text;
    }

    public List<byte[]> ReceivedAudio { get; } = new();

    public Task<string> Transcript(byte[] bytes)
    {
        ReceivedAudio.Add(bytes);
        return Task.FromResult(_text);
    }
}
