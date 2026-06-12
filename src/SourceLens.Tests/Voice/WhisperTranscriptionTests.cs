using NUnit.Framework;
using SourceLens.Domain.Audio;
using SourceLens.Integrations;
using SourceLens.Integrations.Models;
using SourceLens.Integrations.Transcription;

namespace SourceLens.Tests.Voice;

[Explicit("Downloads the Whisper Base GGML model into ./models on first run (network access required) and runs ffmpeg.")]
[Category("Integration")]
public class WhisperTranscriptionTests
{
    private static readonly AudioOptions Options = new() { Rate = 16000, BitsPerSample = 16, Channels = 1 };

    [Test]
    public async Task TranscriptSyntheticWavDoesNotThrowAndYieldsTextOrBlankAudio()
    {
        // 1 секунда тишины + 1 секунда тона 440 Гц, 16 кГц / моно / s16le.
        var pcm = BuildSilence(1.0).Concat(BuildTone(440, 1.0)).ToArray();
        var wav = pcm.ToWav(Options);

        using var transcription = new WhisperTranscription(GgmlModel.Base, "en", new ModelDownloader());

        var result = await transcription.Transcript(wav);

        TestContext.Out.WriteLine($"Whisper result: '{result}'");
        Assert.That(result, Is.Not.Null);
        // На синтетическом аудио без речи допустимы пустой результат, шумовые маркеры
        // вида [BLANK_AUDIO]/(music)/звуковые описания — главное, что транскрипция отработала без исключений.
    }

    private static byte[] BuildSilence(double seconds)
    {
        return new byte[(int)(Options.Rate * seconds) * 2];
    }

    private static byte[] BuildTone(double frequency, double seconds)
    {
        var samples = (int)(Options.Rate * seconds);
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var amplitude = (short)(short.MaxValue * 0.3 * Math.Sin(2 * Math.PI * frequency * i / Options.Rate));
            var sampleBytes = BitConverter.GetBytes(amplitude);
            bytes[i * 2] = sampleBytes[0];
            bytes[i * 2 + 1] = sampleBytes[1];
        }

        return bytes;
    }
}
