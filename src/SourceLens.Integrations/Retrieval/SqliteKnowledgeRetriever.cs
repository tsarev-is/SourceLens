using System.Numerics.Tensors;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Integrations.Retrieval;

public class SqliteKnowledgeRetriever : IKnowledgeRetriever
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly Func<SourceLensContext> _getContext;
    private readonly IEmbedder _embedder;

    public SqliteKnowledgeRetriever(Func<SourceLensContext> getContext, IEmbedder embedder)
    {
        _getContext = getContext;
        _embedder = embedder;
    }

    public async Task<KnowledgeChunk[]> Retrieve(string query, int topK, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return Array.Empty<KnowledgeChunk>();

        var queryVector = await _embedder.Embed(query, EmbedKind.Query, ct);
        if (queryVector.Length != _embedder.Dimensions)
            throw new InvalidOperationException($"Embedder returned {queryVector.Length}-d query vector, expected {_embedder.Dimensions}");

        var entries = await LoadEntries(ct);
        if (entries.Length == 0)
            return Array.Empty<KnowledgeChunk>();

        var topHeap = new PriorityQueue<int, float>(topK);
        for (var i = 0; i < entries.Length; i++)
        {
            var score = TensorPrimitives.CosineSimilarity(queryVector, entries[i].Embedding);
            if (topHeap.Count < topK)
            {
                topHeap.Enqueue(i, score);
            }
            else if (topHeap.TryPeek(out _, out var minScore) && score > minScore)
            {
                topHeap.DequeueEnqueue(i, score);
            }
        }

        var result = new List<(KnowledgeChunk Chunk, float Score)>(topHeap.Count);
        while (topHeap.TryDequeue(out var index, out var score))
        {
            var e = entries[index];
            result.Add((new KnowledgeChunk
            {
                Text = e.Text,
                SourceTitle = e.Title,
                SourceLocation = e.SourceLocation,
                Score = score,
            }, score));
        }
        return result.OrderByDescending(x => x.Score).Select(x => x.Chunk).ToArray();
    }

    private async Task<ChunkEntry[]> LoadEntries(CancellationToken ct)
    {
        await using var ctx = _getContext();
        var rows = await ctx.GetBookChunks(_embedder.ModelId, _embedder.Dimensions, ct);

        var expectedBytes = _embedder.Dimensions * sizeof(float);
        var list = new List<ChunkEntry>(rows.Length);
        foreach (var row in rows)
        {
            if (row.Embedding.Length != expectedBytes)
            {
                Logger.Warn("Skipping chunk with mismatched embedding size: got {0}, expected {1}", row.Embedding.Length, expectedBytes);
                continue;
            }
            list.Add(new ChunkEntry(row.Text, row.Title, row.SourceLocation, BookIngestService.DecodeEmbedding(row.Embedding)));
        }
        return list.ToArray();
    }

    private readonly record struct ChunkEntry(string Text, string Title, string SourceLocation, float[] Embedding);
}
