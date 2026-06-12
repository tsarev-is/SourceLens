using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SourceLens.Domain.Entities.Models;

public class RagSessionItem
{
    protected RagSessionItem() { }

    private RagSessionItem(string? title)
    {
        CreatedAt = DateTime.Now;
        Title = title;
        Deleted = false;
    }

    public int Id { get; }

    public DateTimeOffset CreatedAt { get; }

    public string? Title { get; private set; }

    public bool Deleted { get; private set; }

    public static RagSessionItem Create(string? title = null)
    {
        return new RagSessionItem(title);
    }

    public RagSessionItem SetTitle(string title)
    {
        Title = title;
        return this;
    }

    public void MarkDeleted()
    {
        Deleted = true;
    }

    public static class OrmAccess
    {
        public static string TableName = "rag_sessions";

        public static void Configure(EntityTypeBuilder<RagSessionItem> item)
        {
            item.ToTable(TableName).HasKey(p => p.Id);

            item.Property(p => p.CreatedAt);
            item.Property(p => p.Title);
            item.Property(p => p.Deleted);
        }
    }
}
