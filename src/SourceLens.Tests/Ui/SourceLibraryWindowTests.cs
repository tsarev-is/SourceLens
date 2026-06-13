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
        Assert.That(window.LibraryMetaText.Text, Is.EqualTo("0 sources · 1 collection · 0 chunks indexed"));
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
        Assert.That(window.LibraryMetaText.Text, Is.EqualTo("2 sources · 1 collection · 2,947 chunks indexed"));

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
    public void Sidebar_ShowsAllSourcesAndDefaultCollection()
    {
        var window = new SourceLibraryWindow(BuildManager());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // «All sources» (null) + дефолтная General.
        var names = window.CollectionRows.Select(r => r.Name).ToArray();
        Assert.That(names, Does.Contain("All sources"));
        Assert.That(names, Does.Contain(SourceLibraryManager.DefaultCollectionName));
        Assert.That(window.CollectionRows.Single(r => r.CollectionId == null).Name, Is.EqualTo("All sources"));
        // У дефолтной нет кнопки удаления.
        Assert.That(window.CollectionRows.Single(r => r.Name == SourceLibraryManager.DefaultCollectionName).Delete, Is.Null);
    }

    [AvaloniaTest]
    public async Task CreateCollection_AddsRow_AndSelectsIt()
    {
        var window = new SourceLibraryWindow(BuildManager());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.NewCollectionBox.Text = "Dense retrieval";
        await window.ConfirmNewCollectionAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.That(window.CollectionRows.Any(r => r.Name == "Dense retrieval"), Is.True);
        // Новая коллекция пуста — фильтрованный список показывает empty-state коллекции.
        Assert.That(window.Cards, Is.Empty);
        Assert.That(window.EmptyText.Text, Is.EqualTo("No sources in this collection yet."));
    }

    [AvaloniaTest]
    public async Task SelectingCollection_FiltersDocuments()
    {
        int docId;
        var manager = BuildManager();
        using (var ctx = GetContext())
        {
            docId = ctx.Set<BookDocumentItem>().Add(
                BookDocumentItem.Create("IR Book", "/books/ir-book.pdf", "sha1", "v1", "fake-v1", 4, 5)).Entity.Id;
            ctx.SaveChanges();
        }

        var collectionId = await manager.CreateCollection("Dense");
        await manager.AddToCollection(docId, collectionId);

        var window = new SourceLibraryWindow(manager);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // All sources → виден.
        Assert.That(window.Cards, Has.Count.EqualTo(1));

        // Дефолтная (General) → пусто, т.к. книга уже в пользовательской коллекции.
        var general = window.CollectionRows.Single(r => r.Name == SourceLibraryManager.DefaultCollectionName);
        window.SelectCollectionForTest(general.CollectionId);
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.Cards, Is.Empty);

        // Пользовательская коллекция Dense → книга снова видна.
        window.SelectCollectionForTest(collectionId);
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.Cards, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ToggleMembership_AddsAndRemovesDocumentFromCollection()
    {
        int docId;
        var manager = BuildManager();
        using (var ctx = GetContext())
        {
            docId = ctx.Set<BookDocumentItem>().Add(
                BookDocumentItem.Create("IR Book", "/books/ir-book.pdf", "sha1", "v1", "fake-v1", 4, 5)).Entity.Id;
            ctx.SaveChanges();
        }

        var collectionId = await manager.CreateCollection("Dense");

        var window = new SourceLibraryWindow(manager);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.ToggleMembershipAsync(docId, collectionId, currentlyMember: false);
        Dispatcher.UIThread.RunJobs();
        using (var ctx = GetContext())
            Assert.That(ctx.GetCollectionDocumentIds(collectionId), Is.EquivalentTo(new[] { docId }));

        await window.ToggleMembershipAsync(docId, collectionId, currentlyMember: true);
        Dispatcher.UIThread.RunJobs();
        using (var ctx = GetContext())
            Assert.That(ctx.GetCollectionDocumentIds(collectionId), Is.Empty);
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
        Assert.That(window.LibraryMetaText.Text, Is.EqualTo("1 source · 1 collection · 0 chunks indexed"));

        stub.Gate.SetResult();
        await manager.WhenQueueDrained();
        Dispatcher.UIThread.RunJobs();

        // Стаб ничего не пишет в БД — после завершения карточка исчезает.
        Assert.That(window.Cards, Is.Empty);
        Assert.That(window.EmptyText.IsVisible, Is.True);
    }

    [AvaloniaTest]
    public async Task IndexingProgress_UpdatesCardInPlace_WithoutRebuild()
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

        var cardBefore = window.Cards.Single();
        var progressBefore = cardBefore.Progress;

        stub.LastProgress!.Report(new IngestProgress
        {
            FilePath = file,
            Stage = "embedding",
            ChunksProcessed = 30,
            TotalChunks = 100,
        });
        Dispatcher.UIThread.RunJobs();

        // Тик прогресса не пересоздаёт карточку (нет полного Refresh — нет моргания), а двигает её бар на месте.
        var cardAfter = window.Cards.Single();
        Assert.That(ReferenceEquals(cardAfter, cardBefore), Is.True, "карточка не пересоздаётся на тике прогресса");
        Assert.That(ReferenceEquals(cardAfter.Progress, progressBefore), Is.True);
        Assert.That(cardAfter.Progress!.Value, Is.EqualTo(30));
        Assert.That(cardAfter.Percent!.Text, Is.EqualTo("30%"));

        stub.Gate.SetResult();
        await manager.WhenQueueDrained();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTest]
    public async Task UploadTarget_IsSelectedUserCollection_NullForAllSourcesAndGeneral()
    {
        var manager = BuildManager();
        var collectionId = await manager.CreateCollection("Dense");

        var window = new SourceLibraryWindow(manager);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // «All sources» (по умолчанию) → без явного членства.
        Assert.That(window.UploadTargetCollectionId(), Is.Null);

        // Выбрана пользовательская коллекция → загрузка идёт в неё.
        window.SelectCollectionForTest(collectionId);
        Assert.That(window.UploadTargetCollectionId(), Is.EqualTo(collectionId));

        // Дефолтная General → null (документ без членства и так попадает в General).
        var general = window.CollectionRows.Single(r => r.Name == SourceLibraryManager.DefaultCollectionName);
        window.SelectCollectionForTest(general.CollectionId);
        Assert.That(window.UploadTargetCollectionId(), Is.Null);
    }
}
