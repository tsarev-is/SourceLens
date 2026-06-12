using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;
using SourceLens.Domain.Rag;
using SourceLens.Tests.Rag;

namespace SourceLens.Tests.Managers;

public class SourceLibraryManagerTests
{
    private DbContextOptionsBuilder<SourceLensContext> _builder = null!;
    private string _booksFolder = null!;
    private string _externalFolder = null!;

    [SetUp]
    public void SetUp()
    {
        _builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseInMemoryDatabase(databaseName: $"Library_{Guid.NewGuid()}");
        _booksFolder = Path.Combine(Path.GetTempPath(), "books_" + Guid.NewGuid());
        _externalFolder = Path.Combine(Path.GetTempPath(), "ext_" + Guid.NewGuid());
        Directory.CreateDirectory(_externalFolder);
    }

    [TearDown]
    public void TearDown()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.Database.EnsureDeleted();
        if (Directory.Exists(_booksFolder))
            Directory.Delete(_booksFolder, recursive: true);
        if (Directory.Exists(_externalFolder))
            Directory.Delete(_externalFolder, recursive: true);
    }

    private SourceLensContext GetContext() => Global.CreateContext(_builder);

    private SourceLibraryManager BuildManager(FakeEmbedder? embedder = null)
    {
        var chunkerOptions = new ChunkerOptions { WindowSize = 4, Overlap = 1 };
        var ingestor = new BookIngestService(
            GetContext,
            new IDocumentLoader[] { new FakeLoader() },
            new SlidingWordChunker(chunkerOptions),
            embedder ?? new FakeEmbedder(),
            chunkerOptions);
        return new SourceLibraryManager(GetContext, ingestor, _booksFolder, new[] { ".fake" });
    }

    private string WriteExternalFile(string name, string content = "external source content")
    {
        var path = Path.Combine(_externalFolder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task AddSource_CopiesIntoBooksFolderAndIndexes()
    {
        var manager = BuildManager();
        var source = WriteExternalFile("paper.fake");

        await manager.AddSourceAsync(source);
        await manager.WhenQueueDrained();

        var copy = Path.Combine(_booksFolder, "paper.fake");
        Assert.That(File.Exists(copy), Is.True);
        Assert.That(File.Exists(source), Is.True, "original must stay in place");

        await using var ctx = GetContext();
        var docs = await ctx.Set<BookDocumentItem>().ToArrayAsync();
        Assert.That(docs, Has.Length.EqualTo(1));
        Assert.That(Path.GetFullPath(docs[0].FilePath), Is.EqualTo(Path.GetFullPath(copy)));
        Assert.That(docs[0].ChunkCount, Is.GreaterThan(0));

        var entries = manager.GetEntries();
        Assert.That(entries, Has.Length.EqualTo(1));
        Assert.That(entries[0].Indexing, Is.False);
        Assert.That(entries[0].DocumentId, Is.EqualTo(docs[0].Id));
        Assert.That(entries[0].ChunkCount, Is.EqualTo(docs[0].ChunkCount));
    }

    [Test]
    public async Task AddSource_SameNameSameContent_ReusesExistingFile()
    {
        var manager = BuildManager();
        var source = WriteExternalFile("paper.fake");

        await manager.AddSourceAsync(source);
        await manager.WhenQueueDrained();
        await manager.AddSourceAsync(source);
        await manager.WhenQueueDrained();

        Assert.That(File.Exists(Path.Combine(_booksFolder, "paper.fake")), Is.True);
        Assert.That(File.Exists(Path.Combine(_booksFolder, "paper (1).fake")), Is.False);

        await using var ctx = GetContext();
        Assert.That(await ctx.Set<BookDocumentItem>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddSource_SameNameDifferentContent_GetsUniqueSuffix()
    {
        var manager = BuildManager();
        var first = WriteExternalFile("paper.fake", "content one");
        await manager.AddSourceAsync(first);
        await manager.WhenQueueDrained();

        var otherFolder = Path.Combine(_externalFolder, "other");
        Directory.CreateDirectory(otherFolder);
        var second = Path.Combine(otherFolder, "paper.fake");
        File.WriteAllText(second, "content two — different");
        await manager.AddSourceAsync(second);
        await manager.WhenQueueDrained();

        Assert.That(File.Exists(Path.Combine(_booksFolder, "paper.fake")), Is.True);
        Assert.That(File.Exists(Path.Combine(_booksFolder, "paper (1).fake")), Is.True);

        await using var ctx = GetContext();
        Assert.That(await ctx.Set<BookDocumentItem>().CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task AddSource_SamePathTwice_IngestsOnce()
    {
        var manager = BuildManager();
        var source = WriteExternalFile("paper.fake");

        await Task.WhenAll(manager.AddSourceAsync(source), manager.AddSourceAsync(source));
        await manager.WhenQueueDrained();

        await using var ctx = GetContext();
        Assert.That(await ctx.Set<BookDocumentItem>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task QueueFolderScan_CreatesFolderAndIndexesSupportedFiles()
    {
        var manager = BuildManager();
        Assert.That(Directory.Exists(_booksFolder), Is.False);

        manager.QueueFolderScan();
        Assert.That(Directory.Exists(_booksFolder), Is.True);

        File.WriteAllText(Path.Combine(_booksFolder, "a.fake"), "first book");
        var nested = Path.Combine(_booksFolder, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "b.fake"), "second book");
        File.WriteAllText(Path.Combine(_booksFolder, "ignored.txt"), "unsupported extension");

        manager.QueueFolderScan();
        await manager.WhenQueueDrained();

        await using var ctx = GetContext();
        var docs = await ctx.Set<BookDocumentItem>().ToArrayAsync();
        Assert.That(docs.Select(d => Path.GetFileName(d.FilePath)).OrderBy(n => n), Is.EqualTo(new[] { "a.fake", "b.fake" }));
    }

    [Test]
    public async Task RemoveDocument_DeletesDocAndChunks_KeepsFile()
    {
        var manager = BuildManager();
        var source = WriteExternalFile("paper.fake");
        await manager.AddSourceAsync(source);
        await manager.WhenQueueDrained();

        int documentId;
        string filePath;
        await using (var ctx = GetContext())
        {
            var doc = (await ctx.Set<BookDocumentItem>().ToArrayAsync()).Single();
            documentId = doc.Id;
            filePath = doc.FilePath;
            Assert.That(await ctx.Set<BookChunkItem>().CountAsync(), Is.GreaterThan(0));
        }

        Assert.That(await manager.RemoveDocumentAsync(documentId), Is.True);

        Assert.That(File.Exists(filePath), Is.True, "file in books folder must stay");
        await using (var ctx = GetContext())
        {
            Assert.That(await ctx.Set<BookDocumentItem>().CountAsync(), Is.EqualTo(0));
            Assert.That(await ctx.Set<BookChunkItem>().CountAsync(), Is.EqualTo(0));
        }

        Assert.That(manager.GetEntries(), Is.Empty);
    }

    [Test]
    public async Task RemoveDocument_UnknownId_ReturnsFalse()
    {
        var manager = BuildManager();
        Assert.That(await manager.RemoveDocumentAsync(12345), Is.False);
    }

    [Test]
    public async Task RemoveDocument_WhileItsFileIsQueued_ReturnsFalse()
    {
        Directory.CreateDirectory(_booksFolder);
        var file = Path.Combine(_booksFolder, "busy.fake");
        File.WriteAllText(file, "queued content");

        var stub = new StubIngestor { Gate = new TaskCompletionSource() };
        var manager = new SourceLibraryManager(GetContext, stub, _booksFolder, new[] { ".fake" });

        int documentId;
        await using (var ctx = GetContext())
        {
            var doc = BookDocumentItem.Create("busy", Path.GetFullPath(file), "sha", "v1", "fake-v1", 4, 7);
            ctx.Set<BookDocumentItem>().Add(doc);
            await ctx.SaveChangesAsync();
            documentId = doc.Id;
        }

        // Файл внутри папки книг — AddSourceAsync ставит его в очередь без копирования.
        await manager.AddSourceAsync(file);
        Assert.That(SpinWait.SpinUntil(() => stub.Ingested.Length > 0, 5000), Is.True);

        Assert.That(await manager.RemoveDocumentAsync(documentId), Is.False);

        stub.Gate.SetResult();
        await manager.WhenQueueDrained();
        Assert.That(await manager.RemoveDocumentAsync(documentId), Is.True);
    }

    [Test]
    public async Task GetEntries_OrdersNewestFirst_AndHidesDocBehindInFlightCard()
    {
        var manager = BuildManager();
        var first = WriteExternalFile("first.fake", "first content");
        var second = WriteExternalFile("second.fake", "second content");
        await manager.AddSourceAsync(first);
        await manager.WhenQueueDrained();
        await manager.AddSourceAsync(second);
        await manager.WhenQueueDrained();

        var entries = manager.GetEntries();
        Assert.That(entries.Select(e => e.Title), Is.EqualTo(new[] { "second", "first" }));

        // Документ, чей файл снова в очереди, виден только карточкой «Indexing».
        var stub = new StubIngestor { Gate = new TaskCompletionSource() };
        var gated = new SourceLibraryManager(GetContext, stub, _booksFolder, new[] { ".fake" });
        await gated.AddSourceAsync(Path.Combine(_booksFolder, "first.fake"));
        Assert.That(SpinWait.SpinUntil(() => stub.Ingested.Length > 0, 5000), Is.True);

        var combined = gated.GetEntries();
        Assert.That(combined, Has.Length.EqualTo(2));
        Assert.That(combined[0].Indexing, Is.True);
        Assert.That(combined[0].Title, Is.EqualTo("first"));
        Assert.That(combined[1].Indexing, Is.False);
        Assert.That(combined[1].Title, Is.EqualTo("second"));

        stub.Gate.SetResult();
        await gated.WhenQueueDrained();
    }

    [Test]
    public async Task Changed_RaisedOnQueueProgressAndCompletion()
    {
        var manager = BuildManager();
        var calls = 0;
        manager.Changed += () => Interlocked.Increment(ref calls);

        await manager.AddSourceAsync(WriteExternalFile("paper.fake"));
        await manager.WhenQueueDrained();

        Assert.That(calls, Is.GreaterThanOrEqualTo(2), "at least queued + completed");
    }
}
