using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;
using SourceLens.Domain.Rag.Models;
using SourceLens.Tests.Managers;
using SourceLens.Windows;

namespace SourceLens.Tests.Ui;

public class SourceLibraryWindowTests
{
    private DbContextOptionsBuilder<SourceLensContext> _builder = null!;
    private string _booksFolder = null!;

    [SetUp]
    public void SetUp()
    {
        _builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseInMemoryDatabase("SourceLibraryWindowTests_" + Guid.NewGuid());
        _booksFolder = Path.Combine(Path.GetTempPath(), "books_" + Guid.NewGuid());
    }

    [TearDown]
    public void TearDown()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.Database.EnsureDeleted();
        if (Directory.Exists(_booksFolder))
            Directory.Delete(_booksFolder, recursive: true);
    }

    private SourceLensContext GetContext() => Global.CreateContext(_builder);

    private SourceLibraryManager BuildManager(StubIngestor? ingestor = null)
    {
        return new SourceLibraryManager(GetContext, ingestor ?? new StubIngestor(), _booksFolder);
    }

    [AvaloniaTest]
    public void EmptyState_ShowsPlaceholderAndZeroMeta()
    {
        var window = new SourceLibraryWindow(BuildManager());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.That(window.EmptyText.IsVisible, Is.True);
        Assert.That(window.Cards, Is.Empty);
        Assert.That(window.LibraryMetaText.Text, Is.EqualTo("0 sources · 0 chunks indexed"));
    }

    [AvaloniaTest]
    public void ListsIndexedDocuments_WithMetaAndPill()
    {
        using (var ctx = GetContext())
        {
            ctx.Set<BookDocumentItem>().Add(BookDocumentItem.Create("IR Book", "/books/ir-book.pdf", "sha1", "v1", "fake-v1", 4, 2847));
            ctx.Set<BookDocumentItem>().Add(BookDocumentItem.Create("DPR Paper", "/books/dpr.epub", "sha2", "v1", "fake-v1", 4, 100));
            ctx.SaveChanges();
        }

        var window = new SourceLibraryWindow(BuildManager());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.That(window.EmptyText.IsVisible, Is.False);
        Assert.That(window.Cards, Has.Count.EqualTo(2));
        Assert.That(window.LibraryMetaText.Text, Is.EqualTo("2 sources · 2,947 chunks indexed"));

        // Новый документ (DPR Paper) сверху.
        Assert.That(window.Cards[0].Entry.Title, Is.EqualTo("DPR Paper"));
        Assert.That(window.Cards[0].Meta.Text, Is.EqualTo("EPUB · 100 chunks"));
        Assert.That(window.Cards[1].Meta.Text, Is.EqualTo("PDF · 2,847 chunks"));
        Assert.That(window.Cards[1].Pill.Text, Is.EqualTo("Indexed"));
        Assert.That(window.Cards[1].Remove.IsEnabled, Is.True);
        Assert.That(window.Cards[1].Progress, Is.Null);
    }

    [AvaloniaTest]
    public async Task RemoveDocument_UpdatesListAndDb()
    {
        int documentId;
        using (var ctx = GetContext())
        {
            var doc = BookDocumentItem.Create("IR Book", "/books/ir-book.pdf", "sha1", "v1", "fake-v1", 4, 5);
            ctx.Set<BookDocumentItem>().Add(doc);
            ctx.SaveChanges();
            documentId = doc.Id;
        }

        var window = new SourceLibraryWindow(BuildManager());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.Cards, Has.Count.EqualTo(1));

        await window.RemoveDocumentAsync(documentId);
        Dispatcher.UIThread.RunJobs();

        Assert.That(window.Cards, Is.Empty);
        Assert.That(window.EmptyText.IsVisible, Is.True);
        using (var ctx = GetContext())
            Assert.That(ctx.GetBookDocuments(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task IndexingEntry_ShowsProgressPillAndDisabledRemove()
    {
        Directory.CreateDirectory(_booksFolder);
        var file = Path.Combine(_booksFolder, "new-paper.pdf");
        File.WriteAllText(file, "pdf-ish content");

        var stub = new StubIngestor { Gate = new TaskCompletionSource() };
        var manager = BuildManager(stub);
        var window = new SourceLibraryWindow(manager);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.AddFilesAsync(new[] { file });
        Assert.That(SpinWait.SpinUntil(() => stub.LastProgress != null, 5000), Is.True);
        Dispatcher.UIThread.RunJobs();

        stub.LastProgress!.Report(new IngestProgress
        {
            FilePath = file,
            Stage = "embedding",
            ChunksProcessed = 50,
            TotalChunks = 100,
        });
        Dispatcher.UIThread.RunJobs();

        Assert.That(window.Cards, Has.Count.EqualTo(1));
        var card = window.Cards[0];
        Assert.That(card.Entry.Indexing, Is.True);
        Assert.That(card.Pill.Text, Is.EqualTo("Indexing"));
        Assert.That(card.Meta.Text, Is.EqualTo("Indexing… chunking + embedding"));
        Assert.That(card.Progress, Is.Not.Null);
        Assert.That(card.Progress!.Value, Is.EqualTo(50));
        Assert.That(card.Remove.IsEnabled, Is.False);
        Assert.That(window.LibraryMetaText.Text, Is.EqualTo("1 source · 0 chunks indexed"));

        stub.Gate.SetResult();
        await manager.WhenQueueDrained();
        Dispatcher.UIThread.RunJobs();

        // Стаб ничего не пишет в БД — после завершения карточка исчезает.
        Assert.That(window.Cards, Is.Empty);
        Assert.That(window.EmptyText.IsVisible, Is.True);
    }
}
