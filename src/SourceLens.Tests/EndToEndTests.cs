using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Rag;
using SourceLens.Integrations.DocumentLoaders;
using SourceLens.Integrations.Embeddings;
using SourceLens.Integrations.Models;
using SourceLens.Integrations.Retrieval;

namespace SourceLens.Tests;

/// <summary>
/// Сквозная проверка RAG-конвейера на реальных компонентах: реальный SQLite-файл,
/// реальный LocalOnnxEmbedder (модель из ./models, докачивается идемпотентно),
/// ингест фикстуры .txt и ретрив по вопросу. CLI-агенты (claude/codex) НЕ вызываются.
/// </summary>
[Explicit("Uses the real multilingual-e5-small ONNX model from ./models (downloads it on first run).")]
[Category("Integration")]
public class EndToEndTests
{
    private string _dbPath = null!;
    private DbContextOptionsBuilder<SourceLensContext> _builder = null!;
    private LocalOnnxEmbedder _embedder = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        _builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseSqlite($"Data Source={_dbPath}");
        _embedder = new LocalOnnxEmbedder(new LocalOnnxEmbedderOptions(), new ModelDownloader());
    }

    [TearDown]
    public void TearDown()
    {
        _embedder?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Test]
    public async Task EndToEnd_IngestTxtAndRetrieve_TopChunkMatchesQuestionTopic()
    {
        SourceLensContext GetContext() => new(_builder.Options);

        using (var context = GetContext())
            DatabaseInitializer.Initialize(context);

        // Ингест фикстуры — та же сборка конвейера, что в composition root (App.BuildIngestService).
        var chunkerOptions = new ChunkerOptions { Version = "v1", WindowSize = 120, Overlap = 20 };
        var ingestor = new BookIngestService(
            GetContext,
            new IDocumentLoader[] { new TextDocumentLoader() },
            new SlidingWordChunker(chunkerOptions),
            _embedder,
            chunkerOptions);

        var fixturePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "e2e-handbook.txt");
        Assert.That(File.Exists(fixturePath), $"Fixture not found: {fixturePath}");
        await ingestor.IngestAsync(fixturePath);

        await using (var ctx = GetContext())
        {
            var chunks = await ctx.GetBookChunks(_embedder.ModelId, _embedder.Dimensions);
            Assert.That(chunks, Has.Length.GreaterThanOrEqualTo(3), "Each '#'-section of the fixture should produce its own chunk");
        }

        // Ретрив реальным эмбеддером: вопрос про фотосинтез должен поднять соответствующий раздел в top-1.
        var retriever = new SqliteKnowledgeRetriever(GetContext, _embedder);
        var results = await retriever.Retrieve("How do plants make their food from sunlight?", topK: 3);

        Assert.That(results, Is.Not.Empty);
        Assert.That(results[0].Text, Does.Contain("hotosynthesis"),
            $"Top-1 chunk should be the photosynthesis section, got: {results[0].Text[..Math.Min(120, results[0].Text.Length)]}");
        Assert.That(results[0].SourceTitle, Is.EqualTo("e2e-handbook"));
        Assert.That(results.Select(r => r.Score), Is.Ordered.Descending);
    }
}
