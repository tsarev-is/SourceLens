using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain.Entities;

namespace SourceLens.Tests.Persistence;

/// <summary>
/// Идемпотентность схемы на реальном SQLite: повторный Initialize не падает (DDL-гварды и
/// PRAGMA-защищённые ALTER), а апгрейд «старой» БД без новых таблиц/колонок их добавляет.
/// </summary>
public class DatabaseInitializerTests
{
    private string _dbPath = null!;
    private DbContextOptionsBuilder<SourceLensContext> _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        _builder = new DbContextOptionsBuilder<SourceLensContext>().UseSqlite($"Data Source={_dbPath}");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private SourceLensContext GetContext() => new(_builder.Options);

    [Test]
    public void Initialize_IsIdempotent_AndSeedsDefaultCollectionOnce()
    {
        using (var ctx = GetContext())
            DatabaseInitializer.Initialize(ctx);
        // Повторный прогон (как при каждом старте приложения) не должен падать на ALTER/CREATE.
        using (var ctx = GetContext())
            DatabaseInitializer.Initialize(ctx);

        using (var ctx = GetContext())
            Assert.That(ctx.GetCollectionSummaries().Count(c => c.IsDefault), Is.EqualTo(1));
    }

    [Test]
    public void Initialize_UpgradesOldExchangesTable_AddingScopeColumns()
    {
        // Симулируем «старую» БД: rag_exchanges без ScopeName/ScopeColor.
        using (var ctx = GetContext())
        {
            ctx.Database.OpenConnection();
            ctx.Database.ExecuteSqlRaw(
                """
                CREATE TABLE "rag_exchanges" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_rag_exchanges" PRIMARY KEY AUTOINCREMENT,
                    "RagSessionId" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "Question" TEXT NOT NULL,
                    "Answer" TEXT NULL,
                    "SourcesJson" TEXT NOT NULL,
                    "Deleted" INTEGER NOT NULL
                );
                """);
        }

        using (var ctx = GetContext())
            DatabaseInitializer.Initialize(ctx);

        // Колонки добавлены — запись со снимком области поиска проходит.
        using (var ctx = GetContext())
        {
            var session = ctx.AddRagSession().GetAwaiter().GetResult();
            ctx.SaveChanges();
            var exchange = ctx.AddRagExchange(session, "q", "[]", "Dense", "#6aa6ff").GetAwaiter().GetResult();
            exchange.SetAnswer("a");
            ctx.SaveChanges();
        }

        using (var ctx = GetContext())
        {
            var stored = ctx.GetRagExchanges(ctx.GetRagSessions().Single().Id).Single();
            Assert.That(stored.ScopeName, Is.EqualTo("Dense"));
            Assert.That(stored.ScopeColor, Is.EqualTo("#6aa6ff"));
        }
    }
}
