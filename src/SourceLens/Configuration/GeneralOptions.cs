using System;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Runtime.InteropServices;

namespace SourceLens.Configuration;

/// <summary>
/// Корневые опции приложения. Читаются из appsettings.json (Newtonsoft.Json) в composition root.
/// Помимо DataAnnotations выполняются ручные проверки в <see cref="Validate"/>.
/// </summary>
public class GeneralOptions
{
    [Required]
    public AiOptions AiModel { get; set; } = new();

    public RagOptions Rag { get; set; } = new();

    public TranscriptionOptions Transcription { get; set; } = new();

    public AudioDeviceOptions Audio { get; set; } = new();

    /// <summary>
    /// Ручная валидация согласованности значений. Бросает <see cref="DataException"/> с понятным сообщением.
    /// </summary>
    public void Validate()
    {
        switch (AiModel.Provider)
        {
            case AiOptions.ProviderKind.Claude:
                if (string.IsNullOrWhiteSpace(AiModel.Claude?.BinaryPath))
                    throw new DataException("AiModel.Claude.BinaryPath must be set for Provider=Claude");
                break;
            case AiOptions.ProviderKind.Codex:
                if (string.IsNullOrWhiteSpace(AiModel.Codex?.BinaryPath))
                    throw new DataException("AiModel.Codex.BinaryPath must be set for Provider=Codex");
                break;
        }

        if (Rag.Enabled)
        {
            if (string.IsNullOrWhiteSpace(Rag.BooksFolder))
                throw new DataException("Rag.BooksFolder must be set when Rag.Enabled=true");
            if (Rag.TopK <= 0)
                throw new DataException("Rag.TopK must be positive");
            if (Rag.MinScore is < 0 or > 1)
                throw new DataException("Rag.MinScore must be in [0, 1]");
            if (Rag.MaxRelativeScoreDrop is < 0 or > 1)
                throw new DataException("Rag.MaxRelativeScoreDrop must be in [0, 1]");
            if (Rag.CandidatePoolSize < Rag.TopK)
                throw new DataException("Rag.CandidatePoolSize must be at least Rag.TopK");
            if (Rag.MmrLambda is < 0 or > 1)
                throw new DataException("Rag.MmrLambda must be in [0, 1]");
            if (Rag.MinQueryLength < 0)
                throw new DataException("Rag.MinQueryLength must be non-negative");
            if (Rag.ChunkSize <= 0)
                throw new DataException("Rag.ChunkSize must be positive");
            if (Rag.ChunkOverlap < 0 || Rag.ChunkOverlap >= Rag.ChunkSize)
                throw new DataException("Rag.ChunkOverlap must be in [0, ChunkSize)");
            if (string.IsNullOrWhiteSpace(Rag.ChunkerVersion))
                throw new DataException("Rag.ChunkerVersion must be set");
            if (Rag.EmbeddingProvider == RagOptions.EmbeddingProviderKind.LocalOnnx)
            {
                if (Rag.LocalOnnx.Dimensions <= 0)
                    throw new DataException("Rag.LocalOnnx.Dimensions must be positive");
                if (Rag.LocalOnnx.MaxSequenceLength <= 0)
                    throw new DataException("Rag.LocalOnnx.MaxSequenceLength must be positive");
                if (string.IsNullOrWhiteSpace(Rag.LocalOnnx.ModelIdLabel))
                    throw new DataException("Rag.LocalOnnx.ModelIdLabel must be set");
            }
        }

        if (Rag.HistoryDepth < 0)
            throw new DataException("Rag.HistoryDepth must be non-negative (0 disables dialog context)");
        if (Rag.MaxHistoryChars <= 0)
            throw new DataException("Rag.MaxHistoryChars must be positive");

        if (Transcription.PoolSize <= 0)
            throw new DataException("Transcription.PoolSize must be positive");
        if (Transcription.Threads <= 0)
            throw new DataException("Transcription.Threads must be positive");
        if (string.IsNullOrWhiteSpace(Transcription.Language))
            throw new DataException("Transcription.Language must be set (use \"auto\" for autodetect)");

        if (Audio.Rate <= 0)
            throw new DataException("Audio.Rate must be positive");
        if (Audio.Channels <= 0)
            throw new DataException("Audio.Channels must be positive");
        if (Audio.BitsPerSample is not (8 or 16 or 24 or 32))
            throw new DataException("Audio.BitsPerSample must be 8, 16, 24 or 32");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && string.IsNullOrWhiteSpace(Audio.SourceName))
            throw new DataException("Audio.SourceName must be set on Linux (pactl list short sources; \"default\" works too)");
    }

    public class AiOptions
    {
        /// <summary>
        /// Какой CLI-агент отвечает на вопросы.
        /// </summary>
        public ProviderKind Provider { get; set; } = ProviderKind.Claude;

        public CliEngineOptions Claude { get; set; } = new()
        {
            BinaryPath = "claude",
            ExtraArgs = new[] { "-p", "--output-format", "text" },
            DefaultModel = "sonnet",
        };

        public CliEngineOptions Codex { get; set; } = new()
        {
            BinaryPath = "codex",
            ExtraArgs = Array.Empty<string>(),
            DefaultModel = "gpt-5.5",
        };

        public enum ProviderKind
        {
            Disabled,
            Claude,
            Codex,
        }
    }

    public class CliEngineOptions
    {
        /// <summary>
        /// Путь к бинарю CLI или имя, если он в PATH.
        /// </summary>
        public string BinaryPath { get; set; } = string.Empty;

        /// <summary>
        /// Дополнительные аргументы CLI (до текста промпта).
        /// </summary>
        public string[] ExtraArgs { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Модель по умолчанию, пока пользователь не выбрал другую.
        /// </summary>
        public string DefaultModel { get; set; } = string.Empty;

        /// <summary>
        /// Таймаут одного вызова в секундах.
        /// </summary>
        [Range(1, int.MaxValue)]
        public int TimeoutSeconds { get; set; } = 300;
    }

    public class RagOptions
    {
        /// <summary>
        /// Включить ретрив по индексированным книгам. false — DisabledKnowledgeRetriever (ответы без источников).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Папка с книгами (*.pdf|*.epub|*.txt|*.md), сканируется рекурсивно при старте.
        /// </summary>
        public string BooksFolder { get; set; } = "./books";

        /// <summary>
        /// Сколько чанков подмешивать в промпт.
        /// </summary>
        public int TopK { get; set; } = 5;

        /// <summary>
        /// Абсолютный порог косинусного сходства; чанки ниже отбрасываются. 0 — выключен.
        /// </summary>
        public float MinScore { get; set; }

        /// <summary>
        /// Относительный порог отсечения хвоста (best − value). 0 — выключен.
        /// </summary>
        public float MaxRelativeScoreDrop { get; set; }

        /// <summary>
        /// Размер пула кандидатов на канал (dense/BM25) до слияния и MMR.
        /// </summary>
        public int CandidatePoolSize { get; set; } = 50;

        /// <summary>
        /// Баланс релевантность/разнообразие в MMR (1.0 — только релевантность).
        /// </summary>
        public float MmrLambda { get; set; } = 0.7f;

        /// <summary>
        /// Гибридный поиск: лексический FTS5/BM25 + плотный, слияние через RRF.
        /// </summary>
        public bool HybridSearch { get; set; } = true;

        /// <summary>
        /// Переписывать follow-up вопрос в самодостаточный запрос отдельным вызовом LLM (точнее, но
        /// удваивает задержку для каждого уточняющего вопроса). false — дешёвая эвристика.
        /// </summary>
        public bool RewriteFollowUpQueries { get; set; } = true;

        /// <summary>
        /// Расширять первый вопрос диалога отдельным вызовом LLM перед ретривом (нормализация,
        /// аббревиатуры, синонимы). true — точнее, но +1 LLM-вызов и задержка на старте диалога;
        /// false — первый вопрос идёт в ретрив как есть.
        /// </summary>
        public bool ExpandInitialQuery { get; set; } = true;

        /// <summary>
        /// Минимальная длина вопроса для запуска retrieval; короче — без источников.
        /// </summary>
        public int MinQueryLength { get; set; } = 3;

        /// <summary>
        /// Версия чанкера; изменение ведёт к переиндексации.
        /// </summary>
        public string ChunkerVersion { get; set; } = "v2";

        /// <summary>
        /// Размер чанка в словах (держим под лимитом токенов эмбеддера ~512).
        /// </summary>
        public int ChunkSize { get; set; } = 250;

        /// <summary>
        /// Перекрытие соседних чанков в словах.
        /// </summary>
        public int ChunkOverlap { get; set; } = 50;

        /// <summary>
        /// Максимум пар Q/A в контексте диалога; 0 — контекст отключён (история всё равно пишется).
        /// </summary>
        public int HistoryDepth { get; set; } = 20;

        /// <summary>
        /// Бюджет символов на блок DIALOG_HISTORY; старые пары отбрасываются первыми.
        /// </summary>
        public int MaxHistoryChars { get; set; } = 13000;

        public EmbeddingProviderKind EmbeddingProvider { get; set; } = EmbeddingProviderKind.LocalOnnx;

        public LocalOnnxOptions LocalOnnx { get; set; } = new();

        public enum EmbeddingProviderKind
        {
            LocalOnnx,
        }

        public class LocalOnnxOptions
        {
            /// <summary>
            /// Идентификатор модели для записи в БД (часть ModelId).
            /// </summary>
            public string ModelIdLabel { get; set; } = "multilingual-e5-small";

            public int Dimensions { get; set; } = 384;

            /// <summary>
            /// Максимальная длина последовательности токенов (включая BOS/EOS).
            /// </summary>
            public int MaxSequenceLength { get; set; } = 512;
        }
    }

    public class TranscriptionOptions
    {
        /// <summary>
        /// Размер GGML-модели Whisper.
        /// </summary>
        public TranscriptionModel Model { get; set; } = TranscriptionModel.Base;

        /// <summary>
        /// Язык речи ("auto" — автоопределение).
        /// </summary>
        public string Language { get; set; } = "auto";

        public bool UseGpu { get; set; }

        public int Threads { get; set; } = 4;

        /// <summary>
        /// Размер пула транскрипторов (TranscriptFactory).
        /// </summary>
        public int PoolSize { get; set; } = 1;

        public enum TranscriptionModel
        {
            Tiny,
            Base,
            Small,
            Medium,
            Large,
        }
    }

    public class AudioDeviceOptions
    {
        /// <summary>
        /// Источник звука PulseAudio (Linux/ffmpeg). Получить: pactl list short sources.
        /// </summary>
        public string SourceName { get; set; } = "default";

        public int Rate { get; set; } = 16000;

        public short Channels { get; set; } = 1;

        public short BitsPerSample { get; set; } = 16;
    }
}
