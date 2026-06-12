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

    private async Task SeedCorpus(string modelId, int dim, params (string title, float[] vec, string text, string loc)[] chunks)
    {
        await using var ctx = new SourceLensContext(_builder.Options);
        await ctx.Database.EnsureCreatedAsync();
        var groups = chunks.GroupBy(c => c.title);
        foreach (var g in groups)
        {
            var ordered = g.ToArray();
            var doc = BookDocumentItem.Create(g.Key, $"/tmp/{g.Key}.fake", $"sha-{g.Key}", "v1", modelId, dim, ordered.Length);
            ctx.Set<BookDocumentItem>().Add(doc);
            await ctx.SaveChangesAsync();
            var ordinal = 0;
            foreach (var c in ordered)
            {
                ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(
                    doc.Id, ordinal++, c.text, c.loc, c.vec.Length,
                    BookIngestService.EncodeEmbedding(c.vec)));
            }
            await ctx.SaveChangesAsync();
        }
    }

    [Test]
    public async Task Retrieve_NoCorpus_ReturnsEmpty()
    {
        await using (var ctx = new SourceLensContext(_builder.Options))
            await ctx.Database.EnsureCreatedAsync();

        var retriever = new SqliteKnowledgeRetriever(
            () => new SourceLensContext(_builder.Options),
            new StubEmbedder("any", 3, new[] { 1f, 0f, 0f }));

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

        var retriever = new SqliteKnowledgeRetriever(
            () => new SourceLensContext(_builder.Options),
            new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

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
        await SeedCorpus("model-A", 3,
            ("Book1", new[] { 1f, 0f, 0f }, "wanted", "p.1"));
        await SeedCorpus("model-B", 3,
            ("Book2", new[] { 1f, 0f, 0f }, "wrong-model", "p.1"));

        var retriever = new SqliteKnowledgeRetriever(
            () => new SourceLensContext(_builder.Options),
            new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

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

        var retriever = new SqliteKnowledgeRetriever(
            () => new SourceLensContext(_builder.Options),
            new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever.Retrieve("query", topK: 2);

        Assert.That(result, Has.Length.EqualTo(2));
        Assert.That(result.Select(c => c.Text), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task Retrieve_SameModelDifferentDimensions_FiltersOut()
    {
        await SeedCorpus("model-A", 3, ("Book3d", new[] { 1f, 0f, 0f }, "three-d chunk", "p.1"));
        await SeedCorpus("model-A", 4, ("Book4d", new[] { 1f, 0f, 0f, 0f }, "four-d chunk", "p.1"));

        var retriever3d = new SqliteKnowledgeRetriever(
            () => new SourceLensContext(_builder.Options),
            new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever3d.Retrieve("query", topK: 10);

        Assert.That(result.Select(c => c.Text), Is.EquivalentTo(new[] { "three-d chunk" }));
    }

    [Test]
    public async Task Retrieve_CorruptEmbeddingBytes_AreSkipped()
    {
        await using (var ctx = new SourceLensContext(_builder.Options))
        {
            await ctx.Database.EnsureCreatedAsync();
            var doc = BookDocumentItem.Create("Book", "/tmp/Book.fake", "sha", "v1", "model-A", 3, 2);
            ctx.Set<BookDocumentItem>().Add(doc);
            await ctx.SaveChangesAsync();
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 0, "ok", "p.1", 1, BookIngestService.EncodeEmbedding(new[] { 1f, 0f, 0f })));
            ctx.Set<BookChunkItem>().Add(BookChunkItem.Create(doc.Id, 1, "corrupt", "p.2", 1, new byte[] { 1, 2, 3 }));
            await ctx.SaveChangesAsync();
        }

        var retriever = new SqliteKnowledgeRetriever(
            () => new SourceLensContext(_builder.Options),
            new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        var result = await retriever.Retrieve("query", topK: 5);

        Assert.That(result.Select(c => c.Text), Is.EquivalentTo(new[] { "ok" }));
    }

    [Test]
    public void Retrieve_CancelledToken_Throws()
    {
        var retriever = new SqliteKnowledgeRetriever(
            () => new SourceLensContext(_builder.Options),
            new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(async () => await retriever.Retrieve("query", topK: 5, ct: cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Retrieve_EmptyQuery_ReturnsEmpty()
    {
        await SeedCorpus("model-A", 3, ("Book", new[] { 1f, 0f, 0f }, "a", "p.1"));

        var retriever = new SqliteKnowledgeRetriever(
            () => new SourceLensContext(_builder.Options),
            new StubEmbedder("model-A", 3, new[] { 1f, 0f, 0f }));

        Assert.That(await retriever.Retrieve("", topK: 5), Is.Empty);
        Assert.That(await retriever.Retrieve("ok", topK: 0), Is.Empty);
    }

    [Test]
    public async Task DisabledRetriever_AlwaysReturnsEmpty()
    {
        var retriever = new DisabledKnowledgeRetriever();

        Assert.That(await retriever.Retrieve("any question", topK: 5), Is.Empty);
    }
}
