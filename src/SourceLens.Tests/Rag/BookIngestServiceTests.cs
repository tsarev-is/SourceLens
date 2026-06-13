using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Tests.Rag;

public class BookIngestServiceTests
{
    private DbContextOptionsBuilder<SourceLensContext> _builder = null!;
    private string _file = null!;

    [SetUp]
    public void SetUp()
    {
        _builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseInMemoryDatabase(databaseName: $"Ingest_{Guid.NewGuid()}");
        _file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fake");
        File.WriteAllText(_file, "ignored — loader is fake");
    }

    [TearDown]
    public void TearDown()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.Database.EnsureDeleted();
        if (File.Exists(_file))
            File.Delete(_file);
    }

    private BookIngestService BuildService(FakeEmbedder embedder, ChunkerOptions? chunkerOptions = null)
    {
        chunkerOptions ??= new ChunkerOptions { WindowSize = 4, Overlap = 1 };
        var chunker = new SlidingWordChunker(chunkerOptions);
        return new BookIngestService(
            () => Global.CreateContext(_builder),
            new IDocumentLoader[] { new FakeLoader() },
            chunker,
            embedder,
            chunkerOptions);
    }

    [Test]
    public async Task IngestAsync_StoresDocumentAndChunks()
    {
        var embedder = new FakeEmbedder();
        var service = BuildService(embedder);

        await service.IngestAsync(_file);

        await using var ctx = Global.CreateContext(_builder);
        var docs = await ctx.Set<BookDocumentItem>().ToArrayAsync();
        Assert.That(docs, Has.Length.EqualTo(1));
        Assert.That(docs[0].FilePath, Is.EqualTo(_file));
        Assert.That(docs[0].EmbedderModelId, Is.EqualTo("fake-v1"));
        Assert.That(docs[0].EmbedderDimensions, Is.EqualTo(4));
        Assert.That(docs[0].ChunkerVersion, Is.EqualTo("v2"));
        Assert.That(docs[0].ChunkCount, Is.GreaterThan(0));

        var chunks = await ctx.Set<BookChunkItem>().Where(c => c.DocumentId == docs[0].Id).ToArrayAsync();
        Assert.That(chunks, Has.Length.EqualTo(docs[0].ChunkCount));
        Assert.That(chunks.All(c => c.Embedding.Length == 4 * sizeof(float)), Is.True);
        Assert.That(embedder.Calls, Is.EqualTo(chunks.Length));
    }

    [Test]
    public async Task IngestAsync_ReIngestSameFile_IsIdempotent()
    {
        var embedder = new FakeEmbedder();
        var service = BuildService(embedder);

        await service.IngestAsync(_file);
        var callsAfterFirst = embedder.Calls;

        await service.IngestAsync(_file);

        Assert.That(embedder.Calls, Is.EqualTo(callsAfterFirst), "Second ingest must skip — sha256+chunker+embedder unchanged");

        await using var ctx = Global.CreateContext(_builder);
        Assert.That(await ctx.Set<BookDocumentItem>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task IngestAsync_ChangedChunkerVersion_Reindexes()
    {
        var embedder = new FakeEmbedder();
        await BuildService(embedder, new ChunkerOptions { WindowSize = 4, Overlap = 1, Version = "v1" }).IngestAsync(_file);
        var firstCalls = embedder.Calls;

        await BuildService(embedder, new ChunkerOptions { WindowSize = 4, Overlap = 1, Version = "v2" }).IngestAsync(_file);

        Assert.That(embedder.Calls, Is.GreaterThan(firstCalls));

        await using var ctx = Global.CreateContext(_builder);
        var docs = await ctx.Set<BookDocumentItem>().ToArrayAsync();
        Assert.That(docs, Has.Length.EqualTo(1));
        Assert.That(docs[0].ChunkerVersion, Is.EqualTo("v2"));
    }

    [Test]
    public async Task IngestAsync_ChangedEmbedderModel_Reindexes()
    {
        var first = new FakeEmbedder { ModelId = "fake-v1" };
        await BuildService(first).IngestAsync(_file);

        var second = new FakeEmbedder { ModelId = "fake-v2" };
        await BuildService(second).IngestAsync(_file);

        Assert.That(second.Calls, Is.GreaterThan(0), "Embedder model change must trigger reindex");

        await using var ctx = Global.CreateContext(_builder);
        var docs = await ctx.Set<BookDocumentItem>().ToArrayAsync();
        Assert.That(docs, Has.Length.EqualTo(1), "Stale document must be replaced, not duplicated");
        Assert.That(docs[0].EmbedderModelId, Is.EqualTo("fake-v2"));
        var chunks = await ctx.Set<BookChunkItem>().ToArrayAsync();
        Assert.That(chunks.All(c => c.DocumentId == docs[0].Id), "All chunks must belong to the new document");
    }

    [Test]
    public void IngestAsync_UnsupportedExtension_Throws()
    {
        var embedder = new FakeEmbedder();
        var service = BuildService(embedder);
        var unsupported = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xyz");
        File.WriteAllText(unsupported, "");
        try
        {
            Assert.ThrowsAsync<NotSupportedException>(() => service.IngestAsync(unsupported));
        }
        finally
        {
            File.Delete(unsupported);
        }
    }

    [Test]
    public async Task IngestAsync_ChangedFileContent_ReindexesAndReplacesOldChunks()
    {
        var embedder = new FakeEmbedder();
        var service = BuildService(embedder);

        File.WriteAllText(_file, "original content payload");
        await service.IngestAsync(_file);

        File.WriteAllText(_file, "completely different content here");
        await service.IngestAsync(_file);

        await using (var ctx = Global.CreateContext(_builder))
        {
            var docs = await ctx.Set<BookDocumentItem>().ToArrayAsync();
            Assert.That(docs, Has.Length.EqualTo(1), "Old document should be replaced, not duplicated");
            var chunks = await ctx.Set<BookChunkItem>().ToArrayAsync();
            Assert.That(chunks.All(c => c.DocumentId == docs[0].Id), "All chunks should belong to the new document");
            Assert.That(chunks, Has.Length.EqualTo(docs[0].ChunkCount));
        }
    }

    [Test]
    public async Task IngestAsync_ReportsProgressStages()
    {
        var embedder = new FakeEmbedder();
        var service = BuildService(embedder);
        var stages = new List<string?>();
        var progress = new Progress<IngestProgress>(p =>
        {
            lock (stages)
                stages.Add(p.Stage);
        });

        await service.IngestAsync(_file, progress);
        await Task.Delay(50);

        lock (stages)
        {
            Assert.That(stages, Does.Contain("chunking"));
            Assert.That(stages, Does.Contain("embedding"));
            Assert.That(stages, Does.Contain("completed"));
        }
    }

    [Test]
    public async Task IngestAsync_SecondCall_ReportsSkippedStage()
    {
        var embedder = new FakeEmbedder();
        var service = BuildService(embedder);
        await service.IngestAsync(_file);

        var stages = new List<string?>();
        var progress = new Progress<IngestProgress>(p =>
        {
            lock (stages)
                stages.Add(p.Stage);
        });
        await service.IngestAsync(_file, progress);
        await Task.Delay(50);

        lock (stages)
            Assert.That(stages, Does.Contain("skipped"));
    }

    [Test]
    public void EncodeDecode_Roundtrip()
    {
        var vector = new[] { 0.1f, -0.5f, 12.345f, float.MaxValue, float.MinValue };

        var bytes = BookIngestService.EncodeEmbedding(vector);
        var roundtrip = BookIngestService.DecodeEmbedding(bytes);

        Assert.That(roundtrip, Is.EqualTo(vector));
    }
}
