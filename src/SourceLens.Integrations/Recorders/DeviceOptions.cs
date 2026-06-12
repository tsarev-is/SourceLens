using SourceLens.Domain.Audio;

namespace SourceLens.Integrations.Recorders;

public class DeviceOptions : AudioOptions
{
    /// <summary>
    /// Источник звука PulseAudio. Получить: pactl list short sources.
    /// </summary>
    public required string SourceName { get; init; }
}
