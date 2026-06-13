using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;
using SourceLens.Domain.Rag;
using SourceLens.Integrations.Retrieval;
using SourceLens.Integrations.Stubs;

namespace SourceLens.Tests.Rag;

public class SqliteKnowledgeRetrieverTests
{
    private class StubEmbedder : IEmbedder
    {
        public string ModelId { get; }
        public int Dimensions { get; }
        private readonly float[] _queryVector;

        public StubEmbedder(string modelId, int dimensions, float[] queryVector)
        {
            ModelId = modelId;
            Dimensions = dimensions;
            _queryVector = queryVector;
        }

        public Task<float[]> Embed(string text, EmbedKind kind, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_queryVector);
        }
    }

    // Детерминированные опции для тестов чистого ранжирования: без лексического канала и MMR-перестановок.
    private static RetrievalOptions Ranking => new() { MmrLambda = 1f, HybridSearch = false };

    private string _dbPath = null!;
    private DbContextOptionsBuilder<SourceLensContext> _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        _builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseSqlite($"Data Source={_dbPath}");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private SourceLensContext Context() => new(_builder.Options);

    private SqliteKnowledgeRetriever Retriever(StubEmbedder embedder, RetrievalOptions? options = null)
        => new(Context, embedder, options ?? Ranking);

    /// <summary>
    /// Сеет корпус; ординалы в пределах книги идут с шагом 5 — без смежности (схлопывание не срабатывает).
    /// </summary>
    private async Task SeedCorpus(string modelId, int dim, params (string title, float[] vec, string text, string loc)[] chunks)
    {
        await using var ctx = Context();
        await ctx.Database.EnsureCreatedAsync();
        foreach (var g in chunks.GroupBy(c => c.title))
        {
            var ordered = g.ToArray();
            var doc = BookDocumentItem.Create(g.Key, $"/tmp/{g.Key}.fake", $"sha-{g.Key}", "v2", modelId, dim, ordered.Length);
            ctx.Set<BookDocumentItem>().Add(doc);
            await ctx.SaveChangesAsync();
            var ordinal = 0;
            foreach (var c in ordered)
            {
                ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(
                    doc.Id, ordinal, c.text, c.loc, c.vec.Length,
                    BookIngestService.EncodeEmbedding(c.vec)));
                ordinal += 5;
            }
            await ctx.SaveChangesAsync();
        }
    }

    [Test]
    public async Task Retrieve_NoCorpus_ReturnsEmpty()
    {
        await using (var ctx = Context())
            await ctx.Database.EnsureCreatedAsync();

        var retriever = Retriever(new StubEmbedder("any", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever.Retrieve("query", topK: 5);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Retrieve_RanksByCosineDescending()
    {
        await SeedCorpus("model-A", 3,
            ("Book", new[] { 1f, 0f, 0f }, "exact match", "p.1"),
            ("Book", new[] { 0.7071f, 0.7071f, 0f }, "45 degrees", "p.2"),
            ("Book", new[] { 0f, 0f, 1f }, "orthogonal", "p.3"),
            ("Book", new[] { -1f, 0f, 0f }, "opposite", "p.4"));

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever.Retrieve("query", topK: 3);

        Assert.That(result.Select(c => c.Text), Is.EqualTo(new[] { "exact match", "45 degrees", "orthogonal" }));
        Assert.That(result[0].SourceTitle, Is.EqualTo("Book"));
        Assert.That(result[0].SourceLocation, Is.EqualTo("p.1"));
        Assert.That(result[0].Score, Is.GreaterThan(result[1].Score));
        Assert.That(result[1].Score, Is.GreaterThan(result[2].Score));
    }

    [Test]
    public async Task Retrieve_FiltersByEmbedderModelAndDimensions()
    {
        await SeedCorpus("model-A", 3, ("Book1", new[] { 1f, 0f, 0f }, "wanted", "p.1"));
        await SeedCorpus("model-B", 3, ("Book2", new[] { 1f, 0f, 0f }, "wrong-model", "p.1"));

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever.Retrieve("query", topK: 10);

        Assert.That(result.Select(c => c.Text), Is.EquivalentTo(new[] { "wanted" }));
    }

    [Test]
    public async Task Retrieve_TopKLimitsResults()
    {
        await SeedCorpus("model-A", 3,
            ("Book", new[] { 1f, 0f, 0f }, "a", "p.1"),
            ("Book", new[] { 0.9f, 0.1f, 0f }, "b", "p.2"),
            ("Book", new[] { 0.5f, 0.5f, 0f }, "c", "p.3"),
            ("Book", new[] { 0f, 1f, 0f }, "d", "p.4"));

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever.Retrieve("query", topK: 2);

        Assert.That(result, Has.Length.EqualTo(2));
        Assert.That(result.Select(c => c.Text), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task Retrieve_SameModelDifferentDimensions_FiltersOut()
    {
        await SeedCorpus("model-A", 3, ("Book3d", new[] { 1f, 0f, 0f }, "three-d chunk", "p.1"));
        await SeedCorpus("model-A", 4, ("Book4d", new[] { 1f, 0f, 0f, 0f }, "four-d chunk", "p.1"));

        var retriever3d = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever3d.Retrieve("query", topK: 10);

        Assert.That(result.Select(c => c.Text), Is.EquivalentTo(new[] { "three-d chunk" }));
    }

    [Test]
    public async Task Retrieve_CorruptEmbeddingBytes_AreSkipped()
    {
        await using (var ctx = Context())
        {
            await ctx.Database.EnsureCreatedAsync();
            var doc = BookDocumentItem.Create("Book", "/tmp/Book.fake", "sha", "v2", "model-A", 3, 2);
            ctx.Set<BookDocumentItem>().Add(doc);
            await ctx.SaveChangesAsync();
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 0, "ok", "p.1", 1, BookIngestService.EncodeEmbedding(new[] { 1f, 0f, 0f })));
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 5, "corrupt", "p.2", 1, new byte[] { 1, 2, 3 }));
            await ctx.SaveChangesAsync();
        }

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever.Retrieve("query", topK: 5);

        Assert.That(result.Select(c => c.Text), Is.EquivalentTo(new[] { "ok" }));
    }

    [Test]
    public void Retrieve_CancelledToken_Throws()
    {
        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(async () => await retriever.Retrieve("query", topK: 5, ct: cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Retrieve_EmptyQuery_ReturnsEmpty()
    {
        await SeedCorpus("model-A", 3, ("Book", new[] { 1f, 0f, 0f }, "a", "p.1"));

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        Assert.That(await retriever.Retrieve("", topK: 5), Is.Empty);
        Assert.That(await retriever.Retrieve("ok", topK: 0), Is.Empty);
    }

    // ---------- #4 порог релевантности ----------

    [Test]
    public async Task Retrieve_MinScore_FiltersBelowThreshold_AndReturnsEmptyWhenAllWeak()
    {
        await SeedCorpus("model-A", 3, ("Book", new[] { 0f, 1f, 0f }, "orthogonal", "p.1"));

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }),
            new RetrievalOptions { MinScore = 0.5f, HybridSearch = false });

        var result = await retriever.Retrieve("unrelated", topK: 5);

        Assert.That(result, Is.Empty, "косинус 0 ниже порога 0.5 — нерелевантный чанк не попадает в промпт");
    }

    [Test]
    public async Task Retrieve_MaxRelativeScoreDrop_TrimsTailFarBelowBest()
    {
        await SeedCorpus("model-A", 3,
            ("Book", new[] { 1f, 0f, 0f }, "strong", "p.1"),
            ("Book", new[] { 0.6f, 0.8f, 0f }, "weak-tail", "p.2"));

        // best cosine = 1.0; «weak-tail» = 0.6 < 1.0 − 0.3 → отсекается относительным порогом.
        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }),
            new RetrievalOptions { MaxRelativeScoreDrop = 0.3f, HybridSearch = false, MmrLambda = 1f });

        var result = await retriever.Retrieve("query", topK: 5);

        Assert.That(result.Select(c => c.Text), Is.EqualTo(new[] { "strong" }),
            "хвост, отстающий от лучшего больше чем на MaxRelativeScoreDrop, отброшен");
    }

    [Test]
    public async Task Retrieve_PicksUpNewChunks_AfterCacheInvalidation()
    {
        await SeedCorpus("model-A", 3, ("Book", new[] { 1f, 0f, 0f }, "first", "p.1"));

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var before = await retriever.Retrieve("query", topK: 10); // прогревает кэш векторов
        Assert.That(before.Select(c => c.Text), Is.EquivalentTo(new[] { "first" }));

        // Добавляем чанк в существующую книгу напрямую и сбрасываем кэш (имитируя SourceLibraryManager.Changed).
        await using (var ctx = Context())
        {
            var doc = ctx.GetBookDocuments().Single(d => d.Title == "Book");
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(
                doc.Id, 5, "second", "p.2", 3, BookIngestService.EncodeEmbedding(new[] { 1f, 0f, 0f })));
            await ctx.SaveChangesAsync();
        }
        retriever.InvalidateCache();

        var after = await retriever.Retrieve("query", topK: 10);
        Assert.That(after.Select(c => c.Text), Is.EquivalentTo(new[] { "first", "second" }),
            "после InvalidateCache ретривер видит новые чанки");
    }

    // ---------- #6 схлопывание смежных чанков ----------

    [Test]
    public async Task Retrieve_CollapsesAdjacentOrdinalsIntoOnePassage()
    {
        await using (var ctx = Context())
        {
            await ctx.Database.EnsureCreatedAsync();
            var doc = BookDocumentItem.Create("Book", "/tmp/Book.fake", "sha", "v2", "model-A", 3, 3);
            ctx.Set<BookDocumentItem>().Add(doc);
            await ctx.SaveChangesAsync();
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 0, "first", "p.1", 1, BookIngestService.EncodeEmbedding(new[] { 1f, 0f, 0f })));
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 1, "second", "p.2", 1, BookIngestService.EncodeEmbedding(new[] { 0.99f, 0.01f, 0f })));
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 2, "third", "p.3", 1, BookIngestService.EncodeEmbedding(new[] { 0.98f, 0.02f, 0f })));
            await ctx.SaveChangesAsync();
        }

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever.Retrieve("query", topK: 5);

        Assert.That(result, Has.Length.EqualTo(1), "смежные ординалы одной книги схлопнуты в один пассаж");
        Assert.That(result[0].Text, Is.EqualTo("first second third"));
    }

    // ---------- #6 MMR-разнообразие ----------

    [Test]
    public async Task Retrieve_Mmr_DropsNearDuplicateInFavorOfDiversity()
    {
        await SeedCorpus("model-A", 3,
            ("Book", new[] { 1f, 0f, 0f }, "dupe-a", "p.1"),
            ("Book", new[] { 1f, 0f, 0f }, "dupe-b", "p.2"),
            ("Book", new[] { 0.6f, 0.8f, 0f }, "different", "p.3"));

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }),
            new RetrievalOptions { MmrLambda = 0.2f, HybridSearch = false });

        var result = await retriever.Retrieve("query", topK: 2);
        var texts = result.Select(c => c.Text).ToArray();

        Assert.That(texts, Does.Contain("different"), "разнообразный пассаж занимает слот");
        Assert.That(texts.Count(t => t is "dupe-a" or "dupe-b"), Is.EqualTo(1),
            "из двух почти-дубликатов остаётся только один");
    }

    // ---------- #8 ограничение по книге ----------

    [Test]
    public async Task Retrieve_Scope_RestrictsToSelectedBook()
    {
        await SeedCorpus("model-A", 3,
            ("Alpha", new[] { 1f, 0f, 0f }, "alpha text", "p.1"),
            ("Beta", new[] { 1f, 0f, 0f }, "beta text", "p.1"));

        int alphaId;
        await using (var ctx = Context())
            alphaId = ctx.GetBookDocuments().Single(d => d.Title == "Alpha").Id;

        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var all = await retriever.Retrieve("query", topK: 10);
        Assert.That(all.Select(c => c.Text), Is.EquivalentTo(new[] { "alpha text", "beta text" }));

        var scoped = await retriever.Retrieve("query", topK: 10, RetrievalScope.ForDocuments(alphaId));
        Assert.That(scoped.Select(c => c.Text), Is.EquivalentTo(new[] { "alpha text" }));
    }

    // ---------- #2 гибридный поиск (FTS5/BM25) ----------

    [Test]
    public async Task Retrieve_Hybrid_SurfacesLexicalMatchDenseSearchMisses()
    {
        await using (var ctx = Context())
            DatabaseInitializer.Initialize(ctx);

        await using (var ctx = Context())
        {
            var doc = BookDocumentItem.Create("History", "/tmp/h.fake", "sha", "v2", "model-A", 3, 2);
            ctx.Set<BookDocumentItem>().Add(doc);
            await ctx.SaveChangesAsync();
            // "Napoleon" совпадает только лексически (вектор ортогонален запросу); фотосинтез — наоборот.
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 0, "Napoleon Bonaparte was emperor", "p.1", 1, BookIngestService.EncodeEmbedding(new[] { 0f, 1f, 0f })));
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 5, "photosynthesis and chlorophyll", "p.9", 1, BookIngestService.EncodeEmbedding(new[] { 1f, 0f, 0f })));
            await ctx.SaveChangesAsync();
            await ctx.RebuildDocumentFts(doc.Id);
        }

        // Плотный поиск тянет к фотосинтезу; MinScore 0.5 отсёк бы Наполеона (cos 0),
        // но лексическое попадание освобождено от порога и проявляется через RRF.
        var retriever = Retriever(new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }),
            new RetrievalOptions { HybridSearch = true, MinScore = 0.5f, MmrLambda = 1f });

        var result = await retriever.Retrieve("Napoleon Bonaparte", topK: 5);

        Assert.That(result.Select(c => c.Text), Does.Contain("Napoleon Bonaparte was emperor"),
            "лексический канал поднимает точное совпадение имени, которое плотный поиск пропускает");
    }

    [Test]
    public async Task DisabledRetriever_AlwaysReturnsEmpty()
    {
        var retriever = new DisabledKnowledgeRetriever();

        Assert.That(await retriever.Retrieve("any question", topK: 5), Is.Empty);
    }
}
