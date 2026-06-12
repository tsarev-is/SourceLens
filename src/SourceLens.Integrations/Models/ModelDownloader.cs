using System.Collections.Concurrent;
using System.Diagnostics;
using Whisper.net.Ggml;

namespace SourceLens.Integrations.Models;

public class ModelDownloader
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public const string ModelsDirectory = "models";

    private const string OnnxModelFileName = "multilingual-e5-small.onnx";
    private const string OnnxTokenizerFileName = "sentencepiece.bpe.model";
    private const string OnnxModelUrl = "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/onnx/model.onnx";
    private const string OnnxTokenizerUrl = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/sentencepiece.bpe.model";

    private readonly ConcurrentDictionary<string, Lazy<Task>> _inflight = new();

    public static string GetWhisperPath(GgmlType ggml) =>
        Path.Combine(ModelsDirectory, $"ggml-{ggml.ToString().ToLower()}.bin");

    public static string GetOnnxModelPath() => Path.Combine(ModelsDirectory, OnnxModelFileName);

    public static string GetOnnxTokenizerPath() => Path.Combine(ModelsDirectory, OnnxTokenizerFileName);

    public Task EnsureWhisperAsync(GgmlType ggml)
    {
        return EnsureFile(GetWhisperPath(ggml), dst => CopyWhisper(ggml, dst));
    }

    public Task EnsureOnnxEmbedderAsync()
    {
        var modelTask = EnsureFile(GetOnnxModelPath(), dst => CopyHttp(OnnxModelUrl, dst));
        var tokenizerTask = EnsureFile(GetOnnxTokenizerPath(), dst => CopyHttp(OnnxTokenizerUrl, dst));
        return Task.WhenAll(modelTask, tokenizerTask);
    }

    private Task EnsureFile(string path, Func<Stream, Task> copyToDestination)
    {
        return _inflight.GetOrAdd(path, p => new Lazy<Task>(() =>
        {
            if (File.Exists(p))
            {
                Logger.Info("Model asset already present: {0} ({1} MB)", p, new FileInfo(p).Length / 1024 / 1024);
                return Task.CompletedTask;
            }
            return Task.Run(() => DownloadTo(p, copyToDestination));
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static async Task DownloadTo(string path, Func<Stream, Task> copyToDestination)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmpPath = $"{path}.{Guid.NewGuid():N}.part";
        Logger.Info("Downloading model asset: {0}", path);

        var sw = Stopwatch.StartNew();
        try
        {
            await using (var dst = File.Create(tmpPath))
                await copyToDestination(dst);
            sw.Stop();

            File.Move(tmpPath, path, overwrite: true);
            Logger.Info("Model asset downloaded: {0} ({1} MB; elapsed {2:g})",
                path, new FileInfo(path).Length / 1024 / 1024, sw.Elapsed);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    private static async Task CopyWhisper(GgmlType ggml, Stream destination)
    {
        await using var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggml);
        await src.CopyToAsync(destination);
    }

    private static async Task CopyHttp(string url, Stream destination)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var src = await response.Content.ReadAsStreamAsync();
        await src.CopyToAsync(destination);
    }
}
