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

        // Коллекции источников и связки many-to-many (добавлены после первого релиза).
        context.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "book_collections" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_book_collections" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Color" TEXT NOT NULL,
                "IsDefault" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "Deleted" INTEGER NOT NULL
            );
            """);

        context.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "book_collection_members" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_book_collection_members" PRIMARY KEY AUTOINCREMENT,
                "CollectionId" INTEGER NOT NULL,
                "DocumentId" INTEGER NOT NULL
            );
            """);

        context.Database.ExecuteSqlRaw(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_book_collection_members_CollectionId_DocumentId" ON "book_collection_members" ("CollectionId", "DocumentId");""");
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_book_collection_members_DocumentId" ON "book_collection_members" ("DocumentId");""");

        // Снимок области поиска per-обмен (добавлен после первого релиза): на свежей БД колонки уже есть
        // от EnsureCreated, на старой — добавляем идемпотентно (SQLite ALTER ADD COLUMN падает на дубле).
        AddColumnIfMissing(context, "rag_exchanges", "ScopeName", "TEXT NULL");
        AddColumnIfMissing(context, "rag_exchanges", "ScopeColor", "TEXT NULL");

        // Лексический канал ретрива: FTS5 над текстом чанков (rowid = book_chunks.Id).
        // EnsureCreated не создаёт виртуальные таблицы, поэтому заводим идемпотентно здесь;
        // наполняется в BookIngestService.RebuildDocumentFts при ингесте/переиндексации.
        context.Database.ExecuteSqlRaw(
            $"""CREATE VIRTUAL TABLE IF NOT EXISTS "{SourceLensContext.FtsTableName}" USING fts5(Text);""");

        // Коллекция по умолчанию (General) — бакет для книг без пользовательской коллекции.
        context.EnsureDefaultCollection(SourceLibraryManager.DefaultCollectionName, SourceLibraryManager.DefaultCollectionColor);
    }

    /// <summary>
    /// Добавляет колонку в таблицу, если её ещё нет (проверка через <c>PRAGMA table_info</c>).
    /// Нужно для расширения таблиц, существовавших до этой колонки в уже созданных БД.
    /// </summary>
    private static void AddColumnIfMissing(SourceLensContext context, string table, string column, string definition)
    {
        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
            connection.Open();
        try
        {
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = probe.ExecuteReader();
                while (reader.Read())
                {
                    // table_info columns: cid(0), name(1), type(2), ...
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
            alter.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed)
                connection.Close();
        }
    }
}
