using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SourceLens.Domain.Entities.Models;

public class BookChunkItem
{
    protected BookChunkItem() { }

    private BookChunkItem(int documentId, int ordinal, string text, string sourceLocation, int tokenCount, byte[] embedding)
    {
        DocumentId = documentId;
        Ordinal = ordinal;
        Text = text;
        SourceLocation = sourceLocation;
        TokenCount = tokenCount;
        Embedding = embedding;
    }

    public int Id { get; }

    public int DocumentId { get; private set; }

    public int Ordinal { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public string SourceLocation { get; private set; } = string.Empty;

    public int TokenCount { get; private set; }

    public byte[] Embedding { get; private set; } = Array.Empty<byte>();

    public static BookChunkItem Create(int documentId, int ordinal, string text, string sourceLocation, int tokenCount, byte[] embedding)
    {
        return new BookChunkItem(documentId, ordinal, text, sourceLocation, tokenCount, embedding);
    }

    public static class OrmAccess
    {
        public static string TableName = "book_chunks";

        public static void Configure(EntityTypeBuilder<BookChunkItem> item)
        {
            item.ToTable(TableName).HasKey(p => p.Id);

            item.HasIndex(p => p.DocumentId);

            item.Property(p => p.DocumentId);
            item.Property(p => p.Ordinal);
            item.Property(p => p.Text);
            item.Property(p => p.SourceLocation);
            item.Property(p => p.TokenCount);
            item.Property(p => p.Embedding);
        }
    }
}
