using Microsoft.EntityFrameworkCore;

namespace SourceLens.Domain.Entities;

/// <summary>
/// Creates the database schema. <c>EnsureCreated()</c> builds the full schema for a fresh
/// database but does not add new tables to an existing one, so tables added after the first release are guarded
/// by idempotent <c>CREATE TABLE IF NOT EXISTS</c> statements whose DDL matches what EnsureCreated generates.
/// </summary>
public static class DatabaseInitializer
{
    public static void Initialize(SourceLensContext context)
    {
        context.Database.EnsureCreated();

        if (!context.Database.IsRelational())
        {
            return;
        }

        context.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "rag_sessions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_rag_sessions" PRIMARY KEY AUTOINCREMENT,
                "CreatedAt" TEXT NOT NULL,
                "Title" TEXT NULL,
                "Deleted" INTEGER NOT NULL
            );
            """);

        context.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "rag_exchanges" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_rag_exchanges" PRIMARY KEY AUTOINCREMENT,
                "RagSessionId" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "Question" TEXT NOT NULL,
                "Answer" TEXT NULL,
                "SourcesJson" TEXT NOT NULL,
                "Deleted" INTEGER NOT NULL
            );
            """);

        context.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "app_settings" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_app_settings" PRIMARY KEY,
                "Value" TEXT NOT NULL
            );
            """);

        context.Database.ExecuteSqlRaw(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_rag_exchanges_RagSessionId_Id" ON "rag_exchanges" ("RagSessionId", "Id");""");

        // Лексический канал ретрива: FTS5 над текстом чанков (rowid = book_chunks.Id).
        // EnsureCreated не создаёт виртуальные таблицы, поэтому заводим идемпотентно здесь;
        // наполняется в BookIngestService.RebuildDocumentFts при ингесте/переиндексации.
        context.Database.ExecuteSqlRaw(
            $"""CREATE VIRTUAL TABLE IF NOT EXISTS "{SourceLensContext.FtsTableName}" USING fts5(Text);""");
    }
}
