using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NUnit.Framework;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;

namespace SourceLens.Tests.Persistence;

public class SourceLensContextTests
{
    private DbContextOptionsBuilder<SourceLensContext> _builder = Global.GetBuilder();

    [TearDown]
    public void TearDown()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.Database.EnsureDeleted();
    }

    [Test]
    public async Task AddRagSessionAndExchanges()
    {
        await using var ctx0 = Global.CreateContext(_builder);
        var session = await ctx0.AddRagSession();
        await ctx0.SaveChangesAsync();
        session.SetTitle("What is entropy?");
        var first = await ctx0.AddRagExchange(session, "What is entropy?", "[]");
        first.SetAnswer("A measure of disorder.");
        var second = await ctx0.AddRagExchange(session, "And enthalpy?", "[]");
        second.SetAnswer("Heat content of a system.");
        await ctx0.SaveChangesAsync();

        await using var ctx = Global.CreateContext(_builder);
        var sessions = ctx.GetRagSessions();
        Assert.That(sessions.Length, Is.EqualTo(1));
        Assert.That(sessions[0].Id, Is.Not.EqualTo(0));
        Assert.That(sessions[0].Title, Is.EqualTo("What is entropy?"));
        Assert.That(sessions[0].CreatedAt, Is.Not.EqualTo(default(DateTimeOffset)));

        var exchanges = ctx.GetRagExchanges(sessions[0].Id);
        Assert.That(exchanges.Length, Is.EqualTo(2));
        Assert.That(exchanges.Select(p => p.Question), Is.EqualTo(new[] { "What is entropy?", "And enthalpy?" }));
        Assert.That(exchanges.Select(p => p.Answer), Is.EqualTo(new[] { "A measure of disorder.", "Heat content of a system." }));
        Assert.That(exchanges.Select(p => p.RagSessionId), Is.All.EqualTo(sessions[0].Id));
        Assert.That(exchanges[0].Id, Is.LessThan(exchanges[1].Id));
    }

    [Test]
    public async Task SourcesJsonRoundTrip()
    {
        var sources = new[]
        {
            new { Text = "Entropy always grows.", SourceTitle = "Thermodynamics", SourceLocation = "p.42", Score = 0.91 },
            new { Text = "ΔS ≥ 0 for isolated systems.", SourceTitle = "Physics 101", SourceLocation = "ch.3: laws", Score = 0.84 }
        };
        var sourcesJson = JsonConvert.SerializeObject(sources);

        await using var ctx0 = Global.CreateContext(_builder);
        var session = await ctx0.AddRagSession();
        await ctx0.SaveChangesAsync();
        await ctx0.AddRagExchange(session, "question", sourcesJson);
        await ctx0.SaveChangesAsync();

        await using var ctx = Global.CreateContext(_builder);
        var exchange = ctx.GetRagExchanges(session.Id).Single();
        Assert.That(exchange.SourcesJson, Is.EqualTo(sourcesJson));
    }

    [Test]
    public async Task SoftDeletedExchangeIsHidden()
    {
        await using var ctx0 = Global.CreateContext(_builder);
        var session = await ctx0.AddRagSession();
        await ctx0.SaveChangesAsync();
        var keep = await ctx0.AddRagExchange(session, "keep", "[]");
        var remove = await ctx0.AddRagExchange(session, "remove", "[]");
        await ctx0.SaveChangesAsync();
        remove.MarkDeleted();
        await ctx0.SaveChangesAsync();

        await using var ctx = Global.CreateContext(_builder);
        var exchanges = ctx.GetRagExchanges(session.Id);
        Assert.That(exchanges.Select(p => p.Question), Is.EqualTo(new[] { "keep" }));
        Assert.That(keep.Deleted, Is.False);
    }

    [Test]
    public async Task SoftDeletedSessionIsHidden()
    {
        await using var ctx0 = Global.CreateContext(_builder);
        var visible = await ctx0.AddRagSession("visible");
        var deleted = await ctx0.AddRagSession("deleted");
        await ctx0.SaveChangesAsync();
        deleted.MarkDeleted();
        await ctx0.SaveChangesAsync();

        await using var ctx = Global.CreateContext(_builder);
        var sessions = ctx.GetRagSessions();
        Assert.That(sessions.Select(p => p.Title), Is.EqualTo(new[] { "visible" }));
        Assert.That(ctx.GetLastRagSession()!.Id, Is.EqualTo(visible.Id));
    }

    [Test]
    public async Task GetRagSessionsReturnsNewestFirst()
    {
        await using var ctx0 = Global.CreateContext(_builder);
        await ctx0.AddRagSession("first");
        await ctx0.SaveChangesAsync();
        await ctx0.AddRagSession("second");
        await ctx0.SaveChangesAsync();

        await using var ctx = Global.CreateContext(_builder);
        Assert.That(ctx.GetRagSessions().Select(p => p.Title), Is.EqualTo(new[] { "second", "first" }));
    }

    [Test]
    public async Task GetLastRagSession()
    {
        await using var ctx0 = Global.CreateContext(_builder);
        Assert.That(ctx0.GetLastRagSession(), Is.Null);

        await ctx0.AddRagSession("old");
        await ctx0.SaveChangesAsync();
        var last = await ctx0.AddRagSession("last");
        await ctx0.SaveChangesAsync();

        await using var ctx = Global.CreateContext(_builder);
        Assert.That(ctx.GetLastRagSession()!.Title, Is.EqualTo("last"));
        Assert.That(ctx.GetLastRagSession()!.Id, Is.EqualTo(last.Id));
    }

    [Test]
    public async Task GetBookDocuments_OrdersByAddedAtDescThenIdDesc()
    {
        await using var ctx0 = Global.CreateContext(_builder);
        ctx0.Set<BookDocumentItem>().Add(BookDocumentItem.Create("first", "/books/first.pdf", "sha1", "v1", "fake-v1", 4, 10));
        ctx0.Set<BookDocumentItem>().Add(BookDocumentItem.Create("second", "/books/second.pdf", "sha2", "v1", "fake-v1", 4, 20));
        await ctx0.SaveChangesAsync();

        await using var ctx = Global.CreateContext(_builder);
        Assert.That(ctx.GetBookDocuments().Select(p => p.Title), Is.EqualTo(new[] { "second", "first" }));
    }

    [Test]
    public async Task DeleteBookDocument_RemovesChunksAndDocument()
    {
        int keepId;
        int dropId;
        await using (var ctx0 = Global.CreateContext(_builder))
        {
            var keep = BookDocumentItem.Create("keep", "/books/keep.pdf", "sha-keep", "v1", "fake-v1", 4, 1);
            var drop = BookDocumentItem.Create("drop", "/books/drop.pdf", "sha-drop", "v1", "fake-v1", 4, 2);
            ctx0.Set<BookDocumentItem>().AddRange(keep, drop);
            await ctx0.SaveChangesAsync();
            keepId = keep.Id;
            dropId = drop.Id;

            ctx0.Set<BookChunkItem>().Add(BookChunkItem.Create(keepId, 0, "keep text", "p.1", 2, new byte[16]));
            ctx0.Set<BookChunkItem>().Add(BookChunkItem.Create(dropId, 0, "drop text a", "p.1", 3, new byte[16]));
            ctx0.Set<BookChunkItem>().Add(BookChunkItem.Create(dropId, 1, "drop text b", "p.2", 3, new byte[16]));
            await ctx0.SaveChangesAsync();
        }

        await using (var ctx = Global.CreateContext(_builder))
            Assert.That(await ctx.DeleteBookDocument(dropId), Is.True);

        await using (var ctx = Global.CreateContext(_builder))
        {
            Assert.That(ctx.GetBookDocuments().Select(p => p.Id), Is.EqualTo(new[] { keepId }));
            var chunks = await ctx.Set<BookChunkItem>().ToArrayAsync();
            Assert.That(chunks.Select(c => c.DocumentId), Is.All.EqualTo(keepId));
            Assert.That(chunks, Has.Length.EqualTo(1));
        }
    }

    [Test]
    public async Task DeleteBookDocument_MissingId_ReturnsFalse()
    {
        await using var ctx = Global.CreateContext(_builder);
        Assert.That(await ctx.DeleteBookDocument(12345), Is.False);
    }

    [Test]
    public async Task SettingsUpsert()
    {
        await using var ctx0 = Global.CreateContext(_builder);
        Assert.That(ctx0.GetSetting("engine.provider"), Is.Null);

        ctx0.SetSetting("engine.provider", "Claude");
        await ctx0.SaveChangesAsync();
        ctx0.SetSetting("engine.provider", "Codex");
        ctx0.SetSetting("engine.codex.model", "gpt-5-codex");
        await ctx0.SaveChangesAsync();

        await using var ctx = Global.CreateContext(_builder);
        Assert.That(ctx.GetSetting("engine.provider"), Is.EqualTo("Codex"));
        Assert.That(ctx.GetSetting("engine.codex.model"), Is.EqualTo("gpt-5-codex"));
        Assert.That(ctx.GetSetting("missing"), Is.Null);
    }

    [Test]
    public void DatabaseInitializerAddsRagTablesToExistingDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sourcelens-init-{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<SourceLensContext>().UseSqlite($"Data Source={dbPath}");
        try
        {
            // Simulate a database created before the rag/app_settings tables existed.
            using (var ctx0 = new SourceLensContext(builder.Options))
            {
                ctx0.Database.EnsureCreated();
                ctx0.Database.ExecuteSqlRaw("""DROP INDEX "IX_rag_exchanges_RagSessionId_Id";""");
                ctx0.Database.ExecuteSqlRaw("""DROP TABLE "rag_sessions";""");
                ctx0.Database.ExecuteSqlRaw("""DROP TABLE "rag_exchanges";""");
                ctx0.Database.ExecuteSqlRaw("""DROP TABLE "app_settings";""");
            }

            using (var ctx = new SourceLensContext(builder.Options))
            {
                DatabaseInitializer.Initialize(ctx);
                DatabaseInitializer.Initialize(ctx); // idempotent

                var session = ctx.AddRagSession("after migration").Result;
                ctx.SaveChanges();
                ctx.AddRagExchange(session, "q", "[]").Result.SetAnswer("a");
                ctx.SetSetting("engine.provider", "Claude");
                ctx.SaveChanges();

                Assert.That(ctx.GetRagSessions().Single().Title, Is.EqualTo("after migration"));
                Assert.That(ctx.GetRagExchanges(session.Id).Single().Answer, Is.EqualTo("a"));
                Assert.That(ctx.GetSetting("engine.provider"), Is.EqualTo("Claude"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
