using Microsoft.EntityFrameworkCore;
using SourceLens.Domain.Entities.Models;

namespace SourceLens.Domain.Entities;

public record BookChunkLookup(string Text, string SourceLocation, string Title, byte[] Embedding);

public class SourceLensContext : DbContext
{
    private DbSet<BookDocumentItem> _bookDocumentItems;
    private DbSet<BookChunkItem> _bookChunkItems;
    private DbSet<RagSessionItem> _ragSessionItems;
    private DbSet<RagExchangeItem> _ragExchangeItems;
    private DbSet<AppSettingItem> _appSettingItems;

    public SourceLensContext(DbContextOptions<SourceLensContext> options) : base(options)
    {
        _bookDocumentItems = Set<BookDocumentItem>();
        _bookChunkItems = Set<BookChunkItem>();
        _ragSessionItems = Set<RagSessionItem>();
        _ragExchangeItems = Set<RagExchangeItem>();
        _appSettingItems = Set<AppSettingItem>();
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        BookDocumentItem.OrmAccess.Configure(mb.Entity<BookDocumentItem>());
        BookChunkItem.OrmAccess.Configure(mb.Entity<BookChunkItem>());
        RagSessionItem.OrmAccess.Configure(mb.Entity<RagSessionItem>());
        RagExchangeItem.OrmAccess.Configure(mb.Entity<RagExchangeItem>());
        AppSettingItem.OrmAccess.Configure(mb.Entity<AppSettingItem>());
    }

    public Task<BookChunkLookup[]> GetBookChunks(string embedderModelId, int embedderDimensions, CancellationToken ct = default)
    {
        return (from chunk in _bookChunkItems.AsNoTracking()
                join doc in _bookDocumentItems.AsNoTracking() on chunk.DocumentId equals doc.Id
                where doc.EmbedderModelId == embedderModelId && doc.EmbedderDimensions == embedderDimensions
                select new BookChunkLookup(chunk.Text, chunk.SourceLocation, doc.Title, chunk.Embedding))
            .ToArrayAsync(ct);
    }

    public BookDocumentItem[] GetBookDocuments()
    {
        // SQLite cannot ORDER BY DateTimeOffset; the table is one row per book, so sort client-side.
        return _bookDocumentItems.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(p => p.AddedAt)
            .ThenByDescending(p => p.Id)
            .ToArray();
    }

    public async Task<bool> DeleteBookDocument(int documentId, CancellationToken ct = default)
    {
        var document = await _bookDocumentItems.FirstOrDefaultAsync(p => p.Id == documentId, ct);
        if (document == null)
            return false;

        _bookChunkItems.RemoveRange(_bookChunkItems.Where(c => c.DocumentId == documentId));
        _bookDocumentItems.Remove(document);
        await SaveChangesAsync(ct);
        return true;
    }

    public RagSessionItem[] GetRagSessions()
    {
        return _ragSessionItems.Where(p => !p.Deleted).OrderByDescending(p => p.Id).ToArray();
    }

    public RagExchangeItem[] GetRagExchanges(int sessionId)
    {
        return _ragExchangeItems.Where(p => p.RagSessionId == sessionId && !p.Deleted).OrderBy(p => p.Id).ToArray();
    }

    public RagSessionItem? GetLastRagSession()
    {
        return _ragSessionItems.Where(p => !p.Deleted).OrderByDescending(p => p.Id).FirstOrDefault();
    }

    public RagSessionItem? FindRagSession(int sessionId)
    {
        return _ragSessionItems.FirstOrDefault(p => p.Id == sessionId && !p.Deleted);
    }

    public RagExchangeItem? FindRagExchange(int exchangeId)
    {
        return _ragExchangeItems.FirstOrDefault(p => p.Id == exchangeId && !p.Deleted);
    }

    public async Task<RagSessionItem> AddRagSession(string? title = null)
    {
        var session = (await AddAsync(RagSessionItem.Create(title))).Entity;
        return session;
    }

    public async Task<RagExchangeItem> AddRagExchange(RagSessionItem session, string question, string sourcesJson)
    {
        var exchange = (await AddAsync(RagExchangeItem.Create(session, question, sourcesJson))).Entity;
        return exchange;
    }

    public string? GetSetting(string key)
    {
        return _appSettingItems.AsNoTracking().FirstOrDefault(p => p.Key == key)?.Value;
    }

    public void SetSetting(string key, string value)
    {
        var existing = _appSettingItems.FirstOrDefault(p => p.Key == key);
        if (existing == null)
        {
            _appSettingItems.Add(AppSettingItem.Create(key, value));
        }
        else
        {
            existing.SetValue(value);
        }
    }
}
