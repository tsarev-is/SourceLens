using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SourceLens.Domain.Entities.Models;

public class RagExchangeItem
{
    protected RagExchangeItem() { }

    private RagExchangeItem(RagSessionItem session, string question, string sourcesJson)
    {
        RagSessionId = session.Id;
        CreatedAt = DateTime.Now;
        Question = question;
        SourcesJson = sourcesJson;
        Deleted = false;
    }

    public int Id { get; }

    public int RagSessionId { get; }

    public DateTimeOffset CreatedAt { get; }

    public string Question { get; } = string.Empty;

    public string? Answer { get; private set; }

    public string SourcesJson { get; } = string.Empty;

    public bool Deleted { get; private set; }

    public static RagExchangeItem Create(RagSessionItem session, string question, string sourcesJson)
    {
        return new RagExchangeItem(session, question, sourcesJson);
    }

    public RagExchangeItem SetAnswer(string answer)
    {
        Answer = answer;
        return this;
    }

    public void MarkDeleted()
    {
        Deleted = true;
    }

    public static class OrmAccess
    {
        public static string TableName = "rag_exchanges";

        public static void Configure(EntityTypeBuilder<RagExchangeItem> item)
        {
            item.ToTable(TableName).HasKey(p => p.Id);

            item.HasIndex(r => new { r.RagSessionId, r.Id }).IsUnique();

            item.Property(p => p.CreatedAt);
            item.Property(p => p.Question);
            item.Property(p => p.Answer);
            item.Property(p => p.SourcesJson);
            item.Property(p => p.Deleted);
        }
    }
}
