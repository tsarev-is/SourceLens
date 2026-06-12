using System.Diagnostics;
using System.Text;
using SourceLens.Domain.Audio;

namespace SourceLens.Integrations;

public static class AudioHelpers
{
    /// <summary>
    /// Обернуть raw PCM байты в WAV-контейнер.
    /// </summary>
    public static byte[] ToWav(this byte[] pcmData, AudioOptions audioOptions)
    {
        var byteRate = audioOptions.Rate * audioOptions.Channels * audioOptions.BitsPerSample / 8;
        var blockAlign = (short)(audioOptions.Channels * audioOptions.BitsPerSample / 8);
        var subchunk2Size = pcmData.Length;
        var chunkSize = 36 + subchunk2Size;

        using var fs = new MemoryStream();
        using var writer = new BinaryWriter(fs);

        // RIFF-заголовок
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(chunkSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt-подзаголовок
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Размер подзаголовка
        writer.Write((short)1); // Аудио формат (1 = PCM)
        writer.Write(audioOptions.Channels);
        writer.Write(audioOptions.Rate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(audioOptions.BitsPerSample);

        // data-подзаголовок
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(subchunk2Size);
        writer.Write(pcmData);

        return fs.ToArray();
    }

    /// <summary>
    /// Конвертировать WAV-байты в 16 кГц / моно / PCM 16-bit (формат, ожидаемый Whisper) через ffmpeg.
    /// </summary>
    public static byte[] ConvertTo16Rate(this byte[] inputWavBytes)
    {
        var inputPath = Path.GetTempFileName() + ".wav";
        var outputPath = Path.GetTempFileName() + ".wav";

        try
        {
            File.WriteAllBytes(inputPath, inputWavBytes);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -i \"{inputPath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{outputPath}\"",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {error}");
            }

            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
