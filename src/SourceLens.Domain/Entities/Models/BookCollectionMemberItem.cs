using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SourceLens.Domain.Entities.Models;

/// <summary>
/// Связка many-to-many: документ принадлежит пользовательской коллекции. Коллекция по умолчанию
/// (General) строк здесь не имеет — её состав вычисляется как «документы без членства»
/// (см. <see cref="SourceLensContext.GetCollectionDocumentIds"/>).
/// </summary>
public class BookCollectionMemberItem
{
    protected BookCollectionMemberItem() { }

    private BookCollectionMemberItem(int collectionId, int documentId)
    {
        CollectionId = collectionId;
        DocumentId = documentId;
    }

    public int Id { get; }

    public int CollectionId { get; private set; }

    public int DocumentId { get; private set; }

    public static BookCollectionMemberItem Create(int collectionId, int documentId)
    {
        return new BookCollectionMemberItem(collectionId, documentId);
    }

    public static class OrmAccess
    {
        public static string TableName = "book_collection_members";

        public static void Configure(EntityTypeBuilder<BookCollectionMemberItem> item)
        {
            item.ToTable(TableName).HasKey(p => p.Id);

            item.HasIndex(p => new { p.CollectionId, p.DocumentId }).IsUnique();
            item.HasIndex(p => p.DocumentId);

            item.Property(p => p.CollectionId);
            item.Property(p => p.DocumentId);
        }
    }
}
