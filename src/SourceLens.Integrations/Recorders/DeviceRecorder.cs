using System.Diagnostics;
using SourceLens.Domain.Audio;

namespace SourceLens.Integrations.Recorders;

/// <summary>
/// Запись с микрофона через ffmpeg/PulseAudio (Linux): raw PCM s16le в stdout,
/// чтение пока активен IWhileCondition, затем Kill всего дерева процессов.
/// </summary>
public class DeviceRecorder : IRecorder
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly DeviceOptions _deviceOptions;
    private readonly string _arguments;

    public DeviceRecorder(DeviceOptions deviceOptions)
    {
        _deviceOptions = deviceOptions;
        _arguments = $"-f pulse -i {deviceOptions.SourceName} -ac {deviceOptions.Channels} -ar {deviceOptions.Rate} -f s{deviceOptions.BitsPerSample}le -";
    }

    public async Task<byte[]> Record(IWhileCondition whileCondition)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = _arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();
        Logger.Debug($"Recording started. Source: {_deviceOptions.SourceName};");

        await using var memoryStream = new MemoryStream();
        await using var reader = process.StandardOutput.BaseStream;
        var buffer = new byte[4096];

        while (whileCondition.IsActive)
        {
            var bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                break;

            memoryStream.Write(buffer, 0, bytesRead);
        }
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        Logger.Debug($"Recording finished. Source: {_deviceOptions.SourceName}; Bytes: {memoryStream.Length};");

        return memoryStream.ToArray();
    }

    public AudioOptions GetAudioOptions() => _deviceOptions;
}
