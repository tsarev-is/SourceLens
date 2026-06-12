using System.Diagnostics;
using SourceLens.Domain.Audio;
using SourceLens.Integrations.Models;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace SourceLens.Integrations.Transcription;

/// <summary>
/// Транскрипция через Whisper.net: GGML-модель скачивается через ModelDownloader,
/// по умолчанию CPU (UseGpu=false; в проект включён только CPU-runtime Whisper.net).
/// </summary>
public class WhisperTranscription : ITranscriptor, IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly string _language;
    private readonly int _threads;
    private readonly bool _greedy;
    private readonly Lazy<WhisperFactory> _whisperFactory;

    public WhisperTranscription(
        GgmlModel model,
        string language,
        ModelDownloader downloader,
        bool useGpu = false,
        int threads = 4,
        bool greedy = true)
    {
        _language = language;
        _threads = threads;
        _greedy = greedy;

        var ggmlType = ToGgmlType(model);
        var modelFileName = ModelDownloader.GetWhisperPath(ggmlType);

        _whisperFactory = new Lazy<WhisperFactory>(() =>
        {
            downloader.EnsureWhisperAsync(ggmlType).GetAwaiter().GetResult();

            if (!useGpu)
            {
                // RuntimeOptions — глобальный статик Whisper.net: библиотека грузится один раз на процесс.
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
            }

            var factory = WhisperFactory.FromPath(modelFileName, new WhisperFactoryOptions { UseGpu = useGpu });
            Logger.Info(
                $"Whisper factory ready. Requested order: [{string.Join(",", RuntimeOptions.RuntimeLibraryOrder)}]; " +
                $"LoadedLibrary: {RuntimeOptions.LoadedLibrary}; UseGpu: {useGpu}; Ggml: {ggmlType};");
            return factory;
        });
    }

    public static GgmlType ToGgmlType(GgmlModel model) => model switch
    {
        GgmlModel.Tiny => GgmlType.Tiny,
        GgmlModel.Small => GgmlType.Small,
        GgmlModel.Medium => GgmlType.Medium,
        GgmlModel.Base => GgmlType.Base,
        GgmlModel.Large => GgmlType.LargeV3Turbo,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null)
    };

    public async Task<string> Transcript(byte[] bytes)
    {
        await using var processor = CreateProcessor();

        var result = string.Empty;
        using var memoryStream = new MemoryStream(bytes.ConvertTo16Rate());

        var sw = Stopwatch.StartNew();
        await foreach (var segment in processor.ProcessAsync(memoryStream))
            result += segment.Text;
        sw.Stop();

        Logger.Info($"Transcribed in {sw.ElapsedMilliseconds}ms. Backend: {RuntimeOptions.LoadedLibrary}; Bytes: {bytes.Length}; Chars: {result.Length};");
        return result;
    }

    private WhisperProcessor CreateProcessor()
    {
        var builder = _whisperFactory.Value.CreateBuilder().WithLanguage(_language);

        if (_threads > 0)
            builder = builder.WithThreads(_threads);

        if (_greedy)
            builder = builder.WithGreedySamplingStrategy().ParentBuilder;

        return builder.Build();
    }

    public void Dispose()
    {
        if (_whisperFactory.IsValueCreated)
            _whisperFactory.Value.Dispose();
    }
}
