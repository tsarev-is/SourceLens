using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using SourceLens.Configuration;
using SourceLens.Domain;
using SourceLens.Domain.Audio;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Rag;
using SourceLens.Integrations.Claude;
using SourceLens.Integrations.Cli;
using SourceLens.Integrations.Codex;
using SourceLens.Integrations.DocumentLoaders;
using SourceLens.Integrations.Embeddings;
using SourceLens.Integrations.Models;
using SourceLens.Integrations.Recorders;
using SourceLens.Integrations.Retrieval;
using SourceLens.Integrations.Stubs;
using SourceLens.Integrations.Transcription;

namespace SourceLens.Windows;

public partial class App : Application
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const string SettingsPath = "./appsettings.json";
    private const string SettingsTemplatePath = "./appsettings.template.json";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Logger.Info("Application started");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = BuildRagWindow();
            }
            catch (Exception ex)
            {
                Logger.Fatal(ex, "Startup failed: configuration or composition error");
                desktop.MainWindow = new MainWindow(ex.Message);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private Window BuildRagWindow()
    {
        var options = ReadConfiguration();

        var dbOptions = new DbContextOptionsBuilder<SourceLensContext>()
            .UseSqlite("Data Source=sourcelens.db")
            .Options;
        var getContext = () => new SourceLensContext(dbOptions);
        using (var context = getContext())
            DatabaseInitializer.Initialize(context);
        Logger.Debug("Database initialized");

        var downloader = StartBackgroundModelDownloads(options);

        var (retriever, retrievalOptions, libraryManager) = BuildRetriever(options, getContext, downloader);

        var engineManager = BuildEngineManager(options, getContext);

        var uiRecorder = new UiRecorder(BuildRecorder(options.Audio));

        var transcriptFactory = BuildTranscriptFactory(options.Transcription, downloader);

        var dialogManager = new RagDialogManager(
            getContext,
            () => engineManager.Current,
            retriever,
            retrievalOptions,
            new RagDialogOptions
            {
                HistoryDepth = options.Rag.HistoryDepth,
                MaxHistoryChars = options.Rag.MaxHistoryChars,
            });

        var engineOptions = BuildEngineOptions(options.AiModel);
        Func<EngineOption, Task<CliProbeResult>> probe = async option =>
        {
            var result = await CliEngineProbe.Probe(option.BinaryPath);
            if (!result.Found)
                return result;

            return result with { Models = await CliModelCatalog.List(option.Provider, result.ResolvedPath) };
        };

        return new RagWindow(engineManager, transcriptFactory, uiRecorder, dialogManager, libraryManager, probe, engineOptions);
    }

    private static GeneralOptions ReadConfiguration()
    {
        if (!File.Exists(SettingsPath))
        {
            if (!File.Exists(SettingsTemplatePath))
                throw new FileNotFoundException($"Neither {Path.GetFileName(SettingsPath)} nor {Path.GetFileName(SettingsTemplatePath)} found next to the binary");

            File.Copy(SettingsTemplatePath, SettingsPath);
            Logger.Info("First start: {0} created from {1}", Path.GetFileName(SettingsPath), Path.GetFileName(SettingsTemplatePath));
        }

        var data = File.ReadAllText(SettingsPath);
        var result = JsonConvert.DeserializeObject<GeneralOptions>(data)
                     ?? throw new DataException($"Configuration is empty. Check {Path.GetFileName(SettingsPath)}");

        var validationContext = new ValidationContext(result);
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(result, validationContext, validationResults, true))
            throw new DataException(
                $"Configuration is incorrect: {string.Join("; ", validationResults.Select(r => r.ErrorMessage))}. Check {Path.GetFileName(SettingsPath)}");

        result.Validate();
        return result;
    }

    private static ModelDownloader StartBackgroundModelDownloads(GeneralOptions options)
    {
        var downloader = new ModelDownloader();

        RunInBackground(
            downloader.EnsureWhisperAsync(WhisperTranscription.ToGgmlType(ToGgmlModel(options.Transcription.Model))),
            "Whisper model download");

        if (options.Rag is { Enabled: true, EmbeddingProvider: GeneralOptions.RagOptions.EmbeddingProviderKind.LocalOnnx })
            RunInBackground(downloader.EnsureOnnxEmbedderAsync(), "ONNX embedder download");

        return downloader;
    }

    private static void RunInBackground(Task task, string description)
    {
        task.ContinueWith(
            t => Logger.Error(t.Exception, "{0} failed", description),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static GgmlModel ToGgmlModel(GeneralOptions.TranscriptionOptions.TranscriptionModel model)
    {
        return model switch
        {
            GeneralOptions.TranscriptionOptions.TranscriptionModel.Tiny => GgmlModel.Tiny,
            GeneralOptions.TranscriptionOptions.TranscriptionModel.Base => GgmlModel.Base,
            GeneralOptions.TranscriptionOptions.TranscriptionModel.Small => GgmlModel.Small,
            GeneralOptions.TranscriptionOptions.TranscriptionModel.Medium => GgmlModel.Medium,
            GeneralOptions.TranscriptionOptions.TranscriptionModel.Large => GgmlModel.Large,
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
        };
    }

    private static IEmbedder BuildEmbedder(GeneralOptions.RagOptions rag, ModelDownloader downloader)
    {
        return rag.EmbeddingProvider switch
        {
            GeneralOptions.RagOptions.EmbeddingProviderKind.LocalOnnx => new LocalOnnxEmbedder(
                new LocalOnnxEmbedderOptions
                {
                    ModelIdLabel = rag.LocalOnnx.ModelIdLabel,
                    Dimensions = rag.LocalOnnx.Dimensions,
                    MaxSequenceLength = rag.LocalOnnx.MaxSequenceLength,
                },
                downloader),
            _ => throw new ArgumentOutOfRangeException(nameof(rag), rag.EmbeddingProvider, null),
        };
    }

    private static BookIngestService BuildIngestService(Func<SourceLensContext> getContext, IEmbedder embedder, GeneralOptions.RagOptions rag)
    {
        var chunkerOptions = new ChunkerOptions
        {
            Version = rag.ChunkerVersion,
            WindowSize = rag.ChunkSize,
            Overlap = rag.ChunkOverlap,
        };
        var chunker = new SlidingWordChunker(chunkerOptions);
        var loaders = new IDocumentLoader[] { new PdfDocumentLoader(), new EpubDocumentLoader(), new TextDocumentLoader() };
        return new BookIngestService(getContext, loaders, chunker, embedder, chunkerOptions);
    }

    private static (IKnowledgeRetriever Retriever, RetrievalOptions Options, SourceLibraryManager? Library) BuildRetriever(
        GeneralOptions options, Func<SourceLensContext> getContext, ModelDownloader downloader)
    {
        var retrievalOptions = new RetrievalOptions
        {
            TopK = options.Rag.TopK,
            MinQueryLength = options.Rag.MinQueryLength,
        };

        if (!options.Rag.Enabled)
        {
            Logger.Warn("RAG is disabled: answers will not use indexed sources");
            return (new DisabledKnowledgeRetriever(), retrievalOptions, null);
        }

        var embedder = BuildEmbedder(options.Rag, downloader);
        var retriever = new SqliteKnowledgeRetriever(getContext, embedder);
        var library = new SourceLibraryManager(
            getContext,
            BuildIngestService(getContext, embedder, options.Rag),
            options.Rag.BooksFolder);
        library.QueueFolderScan();
        return (retriever, retrievalOptions, library);
    }

    private static AnswerEngineManager BuildEngineManager(GeneralOptions options, Func<SourceLensContext> getContext)
    {
        var ai = options.AiModel;

        AbstractLlmInferences Factory(string provider, string model)
        {
            var effectiveModel = string.IsNullOrWhiteSpace(model) ? null : model;
            switch (provider)
            {
                case EngineSettings.ClaudeProvider:
                    return new ClaudeClient(
                        ai.Claude.BinaryPath,
                        ai.Claude.ExtraArgs,
                        effectiveModel,
                        workingDirectory: null,
                        TimeSpan.FromSeconds(ai.Claude.TimeoutSeconds > 0 ? ai.Claude.TimeoutSeconds : 300));
                case EngineSettings.CodexProvider:
                    return new CodexClient(
                        ai.Codex.BinaryPath,
                        ai.Codex.ExtraArgs,
                        effectiveModel,
                        workingDirectory: null,
                        TimeSpan.FromSeconds(ai.Codex.TimeoutSeconds > 0 ? ai.Codex.TimeoutSeconds : 300));
                default:
                    Logger.Warn("Answer engine is disabled (provider: '{0}')", provider);
                    return new DisabledLlmClient();
            }
        }

        var defaultProvider = ai.Provider switch
        {
            GeneralOptions.AiOptions.ProviderKind.Claude => EngineSettings.ClaudeProvider,
            GeneralOptions.AiOptions.ProviderKind.Codex => EngineSettings.CodexProvider,
            _ => string.Empty,
        };
        var engineSettings = new EngineSettings(getContext, defaultProvider, ai.Claude.DefaultModel, ai.Codex.DefaultModel);
        return new AnswerEngineManager(Factory, engineSettings);
    }

    private static EngineOption[] BuildEngineOptions(GeneralOptions.AiOptions ai)
    {
        return new[]
        {
            new EngineOption
            {
                Provider = EngineSettings.CodexProvider,
                BinaryPath = ai.Codex.BinaryPath,
                DefaultModel = ai.Codex.DefaultModel,
            },
            new EngineOption
            {
                Provider = EngineSettings.ClaudeProvider,
                BinaryPath = ai.Claude.BinaryPath,
                DefaultModel = ai.Claude.DefaultModel,
            },
        };
    }

    private static IRecorder BuildRecorder(GeneralOptions.AudioDeviceOptions audio)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new NAudioInputRecorder(new AudioOptions
            {
                Rate = audio.Rate,
                Channels = audio.Channels,
                BitsPerSample = audio.BitsPerSample,
            });

        return new DeviceRecorder(new DeviceOptions
        {
            SourceName = audio.SourceName,
            Rate = audio.Rate,
            Channels = audio.Channels,
            BitsPerSample = audio.BitsPerSample,
        });
    }

    private static TranscriptFactory BuildTranscriptFactory(GeneralOptions.TranscriptionOptions transcription, ModelDownloader downloader)
    {
        var transcriptors = Enumerable.Range(0, transcription.PoolSize)
            .Select(_ => (ITranscriptor)new WhisperTranscription(
                ToGgmlModel(transcription.Model),
                transcription.Language,
                downloader,
                transcription.UseGpu,
                transcription.Threads))
            .ToArray();
        return new TranscriptFactory(transcriptors);
    }
}
