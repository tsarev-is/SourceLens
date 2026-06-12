namespace SourceLens.Domain.Audio;

public class AudioOptions
{
    /// <summary>
    /// Частота дискретизации.
    /// </summary>
    public int Rate { get; init; }

    /// <summary>
    /// Глубина дискретизации.
    /// </summary>
    public short BitsPerSample { get; init; }

    /// <summary>
    /// Количество каналов.
    /// </summary>
    public short Channels { get; init; }
}
