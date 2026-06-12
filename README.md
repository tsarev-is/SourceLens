# SourceLens

A RAG-based knowledge platform for students and researchers. Drop books and articles (PDF / EPUB / TXT / MD)
into a folder, ask questions in natural language (typed or by voice), and get source-backed answers with
citations, relevance scores and per-passage summaries. Dialogs are persistent: the conversation context
survives restarts, and the full history is browsable in the UI.

- **Answers** come from CLI agents — **Claude CLI** or **Codex CLI** — using your existing sign-in
  (no API keys stored by the app). The engine and model are switchable at runtime in Settings.
- **Retrieval** is fully local: documents are chunked, embedded with the `multilingual-e5-small` ONNX model
  and indexed into SQLite; top-K passages are selected by cosine similarity.
- **Voice input** is local too: ffmpeg/NAudio recording + Whisper (whisper.cpp via Whisper.net) transcription.
- **Source library** — manage indexed documents from the UI: add files (copied into the books folder,
  deduplicated by SHA-256), watch indexing progress, remove documents from the index.
- **Desktop UI** — Avalonia 11 (dark theme, history panel, sources with score bars, summarize/show-full).

## Download

Prebuilt self-contained builds for **Windows (x64)** and **Linux (x64)** are published on the
[Releases page](../../releases) — built automatically by GitHub Actions on every push to `main`
(`.github/workflows/dotnet-desktop.yml`). They bundle the .NET runtime, so no SDK is needed to run them;
you still need the CLI agents and ffmpeg from the requirements below.

## Requirements

- **.NET SDK** 9/10 (projects target `net9.0`) — only when building from source.
- **Claude CLI** (`claude`) and/or **Codex CLI** (`codex`) installed and signed in — at least the one you
  select as the answer engine. The app only shells out to them; authenticate in your terminal first.
- **ffmpeg** (Linux voice recording via PulseAudio; on Windows NAudio is used instead).
- Internet access on first run to download models (see below).

## Quick start

```bash
cd SourceLens
dotnet build src/SourceLens.sln
dotnet run --project src/SourceLens/SourceLens.csproj
```

1. On first start `appsettings.template.json` is copied to `appsettings.json` next to the binary
   (edit it later to taste; the working copy is gitignored).
2. A `./books` folder is created next to the binary. Put your `.pdf`, `.epub`, `.txt`, `.md` files there
   (subfolders are scanned recursively, indexing runs in the background on startup) — or add files at
   runtime via the **Sources** button, which also shows indexing progress and lets you remove documents
   from the index.
3. On first run the app downloads models into `./models`:
   - `multilingual-e5-small` ONNX embedder + sentencepiece tokenizer (~450 MB) — for RAG;
   - Whisper GGML model (`Base` by default, ~140 MB) — for voice transcription.
4. Ask a question (Ctrl/Cmd+Enter to send) or toggle **Record voice**. Pick the answer engine and
   model in **Settings** — installed CLIs are detected automatically and their model lists are queried
   from the CLI itself; the choice is persisted in the database and survives restarts.

Files created at runtime next to the binary: `sourcelens.db` (index + dialog history + settings),
`models/`, `books/`, `logs/`.

## Configuration (`appsettings.json`)

| Section | Keys | Notes |
|---|---|---|
| `AiModel` | `Provider` (`Claude`/`Codex`/`Disabled`), per-engine `BinaryPath`, `ExtraArgs`, `DefaultModel`, `TimeoutSeconds` | `Provider`/`DefaultModel` are only the initial defaults; the in-app Settings choice (stored in DB) takes priority. Available models are discovered from the CLI at runtime, not configured. |
| `Rag` | `Enabled`, `BooksFolder`, `TopK`, `MinQueryLength`, `ChunkerVersion`, `ChunkSize`, `ChunkOverlap`, `HistoryDepth`, `MaxHistoryChars`, `EmbeddingProvider`, `LocalOnnx{ModelIdLabel,Dimensions,MaxSequenceLength}` | `HistoryDepth=0` disables dialog context (history is still saved). Changing `ChunkerVersion`/embedder settings triggers reindexing. |
| `Transcription` | `Model` (`Tiny`/`Base`/`Small`/`Medium`/`Large`), `Language` (`auto`), `UseGpu`, `Threads`, `PoolSize` | CPU by default. |
| `Audio` | `SourceName`, `Rate`, `Channels`, `BitsPerSample` | `SourceName` is a PulseAudio source on Linux (`pactl list short sources`; `default` works). |

Invalid configuration is reported in a startup error window and in `logs/Error.log`.

## Build & test

```bash
dotnet build src/SourceLens.sln
dotnet test src/SourceLens.Tests/SourceLens.Tests.csproj            # unit + headless UI tests
# integration tests (download models / probe real CLIs), run explicitly:
dotnet test src/SourceLens.Tests/SourceLens.Tests.csproj --filter "Category=Integration"
# end-to-end RAG pipeline (real SQLite + real ONNX embedder):
dotnet test src/SourceLens.Tests/SourceLens.Tests.csproj --filter "FullyQualifiedName~EndToEndTests"
```

## Repository layout

```
.github/workflows/dotnet-desktop.yml  CI: tests + self-contained win-x64/linux-x64 builds → GitHub Release (main only)
src/SourceLens.sln
  SourceLens/               Avalonia UI (RagWindow, SettingsWindow, SourceLibraryWindow) + composition root (App.axaml.cs)
  SourceLens.Domain/        entities (EF Core/SQLite), RAG core (chunker/ingest), RagDialogManager, SourceLibraryManager,
                            LlmContext/PromptCatalog (embedded prompt resources), engine manager, audio abstractions
  SourceLens.Integrations/  Claude/Codex CLI clients, ONNX embedder, SQLite retriever, document loaders,
                            Whisper transcription, recorders, model downloader
  SourceLens.Tests/         NUnit: unit, headless UI (Avalonia.Headless), integration (explicit)
```
