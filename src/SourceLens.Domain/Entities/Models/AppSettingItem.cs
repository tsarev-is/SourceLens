using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SourceLens.Domain.Entities.Models;

public class AppSettingItem
{
    protected AppSettingItem() { }

    private AppSettingItem(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public static AppSettingItem Create(string key, string value)
    {
        return new AppSettingItem(key, value);
    }

    public AppSettingItem SetValue(string value)
    {
        Value = value;
        return this;
    }

    public static class OrmAccess
    {
        public static string TableName = "app_settings";

        public static void Configure(EntityTypeBuilder<AppSettingItem> item)
        {
            item.ToTable(TableName).HasKey(p => p.Key);

            item.Property(p => p.Value);
        }
    }
}
