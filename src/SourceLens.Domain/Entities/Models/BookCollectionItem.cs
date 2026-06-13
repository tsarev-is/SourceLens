using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SourceLens.Domain.Entities.Models;

/// <summary>
/// Именованная коллекция источников (цветная группа книг). Членство — many-to-many через
/// <see cref="BookCollectionMemberItem"/>. Коллекция по умолчанию (<see cref="IsDefault"/>) —
/// «General»: виртуальный бакет для книг без пользовательской коллекции, удалить её нельзя.
/// </summary>
public class BookCollectionItem
{
    protected BookCollectionItem() { }

    private BookCollectionItem(string name, string color, bool isDefault)
    {
        Name = name;
        Color = color;
        IsDefault = isDefault;
        CreatedAt = DateTime.Now;
        Deleted = false;
    }

    public int Id { get; }

    public string Name { get; private set; } = string.Empty;

    public string Color { get; private set; } = string.Empty;

    public bool IsDefault { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public bool Deleted { get; private set; }

    public static BookCollectionItem Create(string name, string color, bool isDefault = false)
    {
        return new BookCollectionItem(name, color, isDefault);
    }

    public BookCollectionItem Rename(string name)
    {
        Name = name;
        return this;
    }

    public void MarkDeleted()
    {
        Deleted = true;
    }

    public static class OrmAccess
    {
        public static string TableName = "book_collections";

        public static void Configure(EntityTypeBuilder<BookCollectionItem> item)
        {
            item.ToTable(TableName).HasKey(p => p.Id);

            item.Property(p => p.Name);
            item.Property(p => p.Color);
            item.Property(p => p.IsDefault);
            item.Property(p => p.CreatedAt);
            item.Property(p => p.Deleted);
        }
    }
}
