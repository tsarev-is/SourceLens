using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SourceLens.Domain.Entities.Models;

public class BookDocumentItem
{
    protected BookDocumentItem() { }

    private BookDocumentItem(string title, string filePath, string sha256, string chunkerVersion, string embedderModelId, int embedderDimensions, int chunkCount)
    {
        Title = title;
        FilePath = filePath;
        Sha256 = sha256;
        ChunkerVersion = chunkerVersion;
        EmbedderModelId = embedderModelId;
        EmbedderDimensions = embedderDimensions;
        ChunkCount = chunkCount;
        AddedAt = DateTime.Now;
    }

    public int Id { get; }

    public string Title { get; private set; } = string.Empty;

    public string FilePath { get; private set; } = string.Empty;

    public string Sha256 { get; private set; } = string.Empty;

    public string ChunkerVersion { get; private set; } = string.Empty;

    public string EmbedderModelId { get; private set; } = string.Empty;

    public int EmbedderDimensions { get; private set; }

    public int ChunkCount { get; private set; }

    public DateTimeOffset AddedAt { get; }

    public static BookDocumentItem Create(string title, string filePath, string sha256, string chunkerVersion, string embedderModelId, int embedderDimensions, int chunkCount)
    {
        return new BookDocumentItem(title, filePath, sha256, chunkerVersion, embedderModelId, embedderDimensions, chunkCount);
    }

    public static class OrmAccess
    {
        public static string TableName = "book_documents";

        public static void Configure(EntityTypeBuilder<BookDocumentItem> item)
        {
            item.ToTable(TableName).HasKey(p => p.Id);

            item.HasIndex(p => p.Sha256);

            item.Property(p => p.Title);
            item.Property(p => p.FilePath);
            item.Property(p => p.Sha256);
            item.Property(p => p.ChunkerVersion);
            item.Property(p => p.EmbedderModelId);
            item.Property(p => p.EmbedderDimensions);
            item.Property(p => p.ChunkCount);
            item.Property(p => p.AddedAt);
        }
    }
}
