using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain.Rag;

public class BookIngestService : IBookIngestor
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly Func<SourceLensContext> _getContext;
    private readonly IReadOnlyList<IDocumentLoader> _loaders;
    private readonly IChunker _chunker;
    private readonly IEmbedder _embedder;
    private readonly ChunkerOptions _chunkerOptions;

    public BookIngestService(Func<SourceLensContext> getContext, IReadOnlyList<IDocumentLoader> loaders, IChunker chunker, IEmbedder embedder, ChunkerOptions chunkerOptions)
    {
        _getContext = getContext;
        _loaders = loaders;
        _chunker = chunker;
        _embedder = embedder;
        _chunkerOptions = chunkerOptions;
    }

    public async Task IngestAsync(string filePath, IProgress<IngestProgress>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Book file not found", filePath);

        var loader = _loaders.FirstOrDefault(l => l.CanHandle(filePath))
            ?? throw new NotSupportedException($"No loader registered for {filePath}");

        var sha256 = await ComputeSha256(filePath, ct);
        progress?.Report(new IngestProgress { FilePath = filePath, Stage = "checking" });

        await using (var ctx = _getContext())
        {
            var existing = await ctx.Set<BookDocumentItem>()
                .Where(d => d.FilePath == filePath || d.Sha256 == sha256)
                .ToArrayAsync(ct);

            var upToDate = existing.FirstOrDefault(d =>
                d.Sha256 == sha256 &&
                d.ChunkerVersion == _chunkerOptions.Version &&
                d.EmbedderModelId == _embedder.ModelId &&
                d.EmbedderDimensions == _embedder.Dimensions);
            if (upToDate != null)
            {
                Logger.Info("Book {0} already indexed and up to date ({1} chunks)", filePath, upToDate.ChunkCount);
                progress?.Report(new IngestProgress { FilePath = filePath, Stage = "skipped", ChunksProcessed = upToDate.ChunkCount, TotalChunks = upToDate.ChunkCount });
                return;
            }

            foreach (var stale in existing)
            {
                // Чистим FTS-строки пока чанки ещё в БД (RemoveRange отложен до SaveChanges).
                await ctx.RemoveDocumentFts(stale.Id, ct);
                var chunkSet = ctx.Set<BookChunkItem>().Where(c => c.DocumentId == stale.Id);
                ctx.Set<BookChunkItem>().RemoveRange(chunkSet);
                ctx.Set<BookDocumentItem>().Remove(stale);
            }
            if (existing.Length > 0)
                await ctx.SaveChangesAsync(ct);
        }

        progress?.Report(new IngestProgress { FilePath = filePath, Stage = "chunking" });

        var chunks = new List<Chunk>();
        await foreach (var chunk in _chunker.Chunk(loader.LoadSegments(filePath, ct), ct))
            chunks.Add(chunk);

        progress?.Report(new IngestProgress { FilePath = filePath, Stage = "embedding", TotalChunks = chunks.Count });

        var embeddings = new byte[chunks.Count][];
        for (var i = 0; i < chunks.Count; i++)
        {
            var vector = await _embedder.Embed(chunks[i].Text, EmbedKind.Passage, ct);
            embeddings[i] = EncodeEmbedding(vector);
            if ((i + 1) % 50 == 0 || i + 1 == chunks.Count)
                progress?.Report(new IngestProgress { FilePath = filePath, Stage = "embedding", ChunksProcessed = i + 1, TotalChunks = chunks.Count });
        }

        await using (var ctx = _getContext())
        {
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;

            var document = BookDocumentItem.Create(
                title: Path.GetFileNameWithoutExtension(filePath),
                filePath: filePath,
                sha256: sha256,
                chunkerVersion: _chunkerOptions.Version,
                embedderModelId: _embedder.ModelId,
                embedderDimensions: _embedder.Dimensions,
                chunkCount: chunks.Count);
            ctx.Set<BookDocumentItem>().Add(document);
            await ctx.SaveChangesAsync(ct);

            for (var i = 0; i < chunks.Count; i++)
            {
                var item = BookChunkItem.Create(
                    documentId: document.Id,
                    ordinal: chunks[i].Ordinal,
                    text: chunks[i].Text,
                    sourceLocation: chunks[i].SourceLocation,
                    tokenCount: chunks[i].TokenCount,
                    embedding: embeddings[i]);
                ctx.Set<BookChunkItem>().Add(item);

                if ((i + 1) % 500 == 0)
                {
                    await ctx.SaveChangesAsync(ct);
                    foreach (var entry in ctx.ChangeTracker.Entries<BookChunkItem>().ToArray())
                        entry.State = EntityState.Detached;
                }
            }
            await ctx.SaveChangesAsync(ct);

            // Наполняем лексический индекс по уже сохранённым (с реальными id) чанкам документа.
            await ctx.RebuildDocumentFts(document.Id, ct);
        }

        Logger.Info("Indexed {0}: {1} chunks", filePath, chunks.Count);
        progress?.Report(new IngestProgress { FilePath = filePath, Stage = "completed", ChunksProcessed = chunks.Count, TotalChunks = chunks.Count });
    }

    private static async Task<string> ComputeSha256(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    public static byte[] EncodeEmbedding(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] DecodeEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
