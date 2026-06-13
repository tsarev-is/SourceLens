using System.Numerics.Tensors;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Integrations.Retrieval;

/// <summary>
/// Гибридный ретривер: плотный поиск (косинус по кэшу нормализованных векторов) + лексический канал
/// (FTS5/BM25), слияние через Reciprocal Rank Fusion, абсолютный/относительный порог релевантности,
/// схлопывание смежных чанков и MMR для разнообразия. Векторы кэшируются в памяти и перезагружаются,
/// только когда меняется снимок корпуса (count, maxId).
/// </summary>
public class SqliteKnowledgeRetriever : IKnowledgeRetriever, IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const double RrfK = 60.0;
    private const int MmrCandidatePool = 30;

    private readonly Func<SourceLensContext> _getContext;
    private readonly IEmbedder _embedder;
    private readonly RetrievalOptions _options;

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private volatile VectorCache? _cache;

    public SqliteKnowledgeRetriever(Func<SourceLensContext> getContext, IEmbedder embedder, RetrievalOptions? options = null)
    {
        _getContext = getContext;
        _embedder = embedder;
        _options = options ?? new RetrievalOptions();
    }

    /// <summary>
    /// Сбросить кэш векторов (например, по событию изменения библиотеки). Безопасно.
    /// </summary>
    public void InvalidateCache() => _cache = null;

    public async Task<KnowledgeChunk[]> Retrieve(string query, int topK, RetrievalScope? scope = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return Array.Empty<KnowledgeChunk>();

        var queryVector = await _embedder.Embed(query, EmbedKind.Query, ct);
        if (queryVector.Length != _embedder.Dimensions)
            throw new InvalidOperationException($"Embedder returned {queryVector.Length}-d query vector, expected {_embedder.Dimensions}");

        var cache = await EnsureCache(ct);
        if (cache.Length == 0)
            return Array.Empty<KnowledgeChunk>();

        var dims = _embedder.Dimensions;
        var scopeIds = scope?.DocumentIds is { Count: > 0 } ? new HashSet<int>(scope.DocumentIds) : null;

        // Косинус (= dot, т.к. оба вектора L2-нормализованы) для всех чанков в области поиска.
        var qSpan = queryVector.AsSpan();
        var cosine = new Dictionary<int, float>(cache.Length);
        var indexByChunk = new Dictionary<int, int>(cache.Length);
        var inScope = new List<int>(cache.Length);
        for (var i = 0; i < cache.Length; i++)
        {
            if (scopeIds != null && !scopeIds.Contains(cache.DocumentIds[i]))
                continue;
            inScope.Add(i);
            var id = cache.ChunkIds[i];
            cosine[id] = TensorPrimitives.Dot(qSpan, cache.Vectors.AsSpan(i * dims, dims));
            indexByChunk[id] = i;
        }
        if (inScope.Count == 0)
            return Array.Empty<KnowledgeChunk>();

        ct.ThrowIfCancellationRequested();

        var pool = Math.Max(topK, _options.CandidatePoolSize);

        // Плотный канал: топ-pool по косинусу.
        var denseRanked = inScope
            .OrderByDescending(i => cosine[cache.ChunkIds[i]])
            .Take(pool)
            .Select(i => cache.ChunkIds[i])
            .ToList();

        // Лексический канал (BM25), отфильтрованный по области и (model, dims).
        var lexIds = Array.Empty<int>();
        if (_options.HybridSearch)
        {
            await using var ctx = _getContext();
            var raw = await ctx.SearchLexicalChunkIds(query, _embedder.ModelId, dims, scope?.DocumentIds, pool, ct);
            lexIds = raw.Where(indexByChunk.ContainsKey).ToArray();
        }
        var lexSet = new HashSet<int>(lexIds);

        // Reciprocal Rank Fusion плотного и лексического рангов.
        var fused = new Dictionary<int, double>();
        for (var r = 0; r < denseRanked.Count; r++)
            fused[denseRanked[r]] = fused.GetValueOrDefault(denseRanked[r]) + 1.0 / (RrfK + r + 1);
        for (var r = 0; r < lexIds.Length; r++)
            fused[lexIds[r]] = fused.GetValueOrDefault(lexIds[r]) + 1.0 / (RrfK + r + 1);

        var candidateIds = fused.Keys.ToList();
        if (candidateIds.Count == 0)
            return Array.Empty<KnowledgeChunk>();

        // Порог релевантности (лексические попадания освобождены — точное совпадение терминов значимо).
        var bestCosine = candidateIds.Max(id => cosine[id]);
        candidateIds = candidateIds.Where(id => PassesThreshold(id, cosine[id], bestCosine, lexSet)).ToList();
        if (candidateIds.Count == 0)
        {
            Logger.Info("Retrieve: {0} candidates filtered out by relevance threshold (best cosine {1:0.000})", fused.Count, bestCosine);
            return Array.Empty<KnowledgeChunk>();
        }

        // Схлопываем смежные ординалы одной книги в один расширенный пассаж.
        var groups = CollapseAdjacent(candidateIds, cache, indexByChunk, cosine, fused);

        // MMR по топ-кандидатам для разнообразия источников.
        var mmrInput = groups.OrderByDescending(g => g.Fused).Take(Math.Max(topK, MmrCandidatePool)).ToList();
        var selected = ApplyMmr(mmrInput, cache, dims, topK);

        // Для отображения сортируем по итоговому рангу (fused) убыванием: иначе сильное лексическое
        // попадание (косинус ≈ 0, но высокий BM25) уезжало бы в конец списка источников.
        selected = selected.OrderByDescending(g => g.Fused).ToList();

        var memberIds = selected.SelectMany(g => g.MemberIds).ToList();
        await using (var textCtx = _getContext())
        {
            var texts = await textCtx.GetBookChunkTexts(memberIds, ct);
            return selected.Select(g => BuildChunk(g, texts)).ToArray();
        }
    }

    private bool PassesThreshold(int id, float score, float bestCosine, HashSet<int> lexSet)
    {
        if (lexSet.Contains(id))
            return true;
        if (_options.MinScore > 0 && score < _options.MinScore)
            return false;
        if (_options.MaxRelativeScoreDrop > 0 && score < bestCosine - _options.MaxRelativeScoreDrop)
            return false;
        return true;
    }

    private static List<PassageGroup> CollapseAdjacent(List<int> candidateIds, VectorCache cache,
        Dictionary<int, int> indexByChunk, Dictionary<int, float> cosine, Dictionary<int, double> fused)
    {
        var groups = new List<PassageGroup>();
        foreach (var docGroup in candidateIds
                     .GroupBy(id => cache.DocumentIds[indexByChunk[id]]))
        {
            var ordered = docGroup
                .OrderBy(id => cache.Ordinals[indexByChunk[id]])
                .ToList();

            var run = new List<int>();
            var prevOrdinal = int.MinValue;
            foreach (var id in ordered)
            {
                var ordinal = cache.Ordinals[indexByChunk[id]];
                if (run.Count > 0 && ordinal != prevOrdinal + 1)
                {
                    groups.Add(BuildGroup(run, cache, indexByChunk, cosine, fused));
                    run = new List<int>();
                }
                run.Add(id);
                prevOrdinal = ordinal;
            }
            if (run.Count > 0)
                groups.Add(BuildGroup(run, cache, indexByChunk, cosine, fused));
        }
        return groups;
    }

    private static PassageGroup BuildGroup(List<int> memberIds, VectorCache cache,
        Dictionary<int, int> indexByChunk, Dictionary<int, float> cosine, Dictionary<int, double> fused)
    {
        var repId = memberIds.MaxBy(id => cosine[id]);
        return new PassageGroup
        {
            DocumentId = cache.DocumentIds[indexByChunk[memberIds[0]]],
            MemberIds = memberIds,
            RepCacheIndex = indexByChunk[repId],
            Cosine = memberIds.Max(id => cosine[id]),
            Fused = memberIds.Max(id => fused[id]),
        };
    }

    private List<PassageGroup> ApplyMmr(List<PassageGroup> candidates, VectorCache cache, int dims, int topK)
    {
        if (candidates.Count <= 1)
            return candidates.Take(topK).ToList();

        // Нормируем релевантность (fused) в [0,1], чтобы она была сопоставима с косинусной близостью.
        var maxFused = candidates.Max(g => g.Fused);
        var minFused = candidates.Min(g => g.Fused);
        var span = maxFused - minFused;
        double Relevance(PassageGroup g) => span <= 1e-12 ? 1.0 : (g.Fused - minFused) / span;

        var lambda = Math.Clamp(_options.MmrLambda, 0f, 1f);
        var remaining = new List<PassageGroup>(candidates);
        var selected = new List<PassageGroup>(Math.Min(topK, candidates.Count));

        while (selected.Count < topK && remaining.Count > 0)
        {
            PassageGroup? best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var g in remaining)
            {
                double penalty = 0;
                foreach (var s in selected)
                {
                    var sim = TensorPrimitives.Dot(
                        cache.Vectors.AsSpan(g.RepCacheIndex * dims, dims),
                        cache.Vectors.AsSpan(s.RepCacheIndex * dims, dims));
                    if (sim > penalty)
                        penalty = sim;
                }
                var score = lambda * Relevance(g) - (1 - lambda) * penalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = g;
                }
            }

            if (best == null)
                break;
            selected.Add(best);
            remaining.Remove(best);
        }
        return selected;
    }

    private static KnowledgeChunk BuildChunk(PassageGroup group, Dictionary<int, BookChunkText> texts)
    {
        var parts = group.MemberIds
            .Where(texts.ContainsKey)
            .Select(id => texts[id].Text);
        var meta = texts.TryGetValue(group.MemberIds[0], out var first) ? first : null;
        return new KnowledgeChunk
        {
            Text = string.Join(" ", parts),
            SourceTitle = meta?.Title ?? string.Empty,
            SourceLocation = meta?.SourceLocation ?? string.Empty,
            Score = group.Cosine,
        };
    }

    private async Task<VectorCache> EnsureCache(CancellationToken ct)
    {
        (int Count, int MaxId) stat;
        await using (var statCtx = _getContext())
            stat = await statCtx.GetChunkStat(_embedder.ModelId, _embedder.Dimensions, ct);

        var current = _cache;
        if (IsFresh(current, stat))
            return current!;

        await _cacheLock.WaitAsync(ct);
        try
        {
            current = _cache;
            if (IsFresh(current, stat))
                return current!;

            current = await LoadCache(stat, ct);
            _cache = current;
            return current;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    // Дешёвый backstop: (count, maxId) ловит почти все изменения корпуса, но НЕ переиспользование
    // rowid (удалить чанк с максимальным id и тут же вставить новый с тем же id). Основной триггер
    // инвалидации — событие SourceLibraryManager.Changed → InvalidateCache; этот снимок лишь подстраховка.
    private bool IsFresh(VectorCache? cache, (int Count, int MaxId) stat)
    {
        return cache != null
               && cache.ModelId == _embedder.ModelId
               && cache.Dims == _embedder.Dimensions
               && cache.KeyCount == stat.Count
               && cache.KeyMaxId == stat.MaxId;
    }

    private async Task<VectorCache> LoadCache((int Count, int MaxId) stat, CancellationToken ct)
    {
        var dims = _embedder.Dimensions;
        var expectedBytes = dims * sizeof(float);

        await using var ctx = _getContext();
        var rows = await ctx.GetBookChunkVectors(_embedder.ModelId, dims, ct);

        var ids = new List<int>(rows.Length);
        var docs = new List<int>(rows.Length);
        var ords = new List<int>(rows.Length);
        var vectors = new float[rows.Length * dims];
        var n = 0;
        foreach (var row in rows)
        {
            if (row.Embedding.Length != expectedBytes)
            {
                Logger.Warn("Skipping chunk {0} with mismatched embedding size: got {1}, expected {2}", row.ChunkId, row.Embedding.Length, expectedBytes);
                continue;
            }
            Buffer.BlockCopy(row.Embedding, 0, vectors, n * expectedBytes, expectedBytes);
            ids.Add(row.ChunkId);
            docs.Add(row.DocumentId);
            ords.Add(row.Ordinal);
            n++;
        }

        Logger.Debug("Embedding cache loaded: {0} vectors for {1}", n, _embedder.ModelId);
        return new VectorCache
        {
            ModelId = _embedder.ModelId,
            Dims = dims,
            KeyCount = stat.Count,
            KeyMaxId = stat.MaxId,
            Length = n,
            ChunkIds = ids.ToArray(),
            DocumentIds = docs.ToArray(),
            Ordinals = ords.ToArray(),
            Vectors = vectors,
        };
    }

    private sealed class VectorCache
    {
        public required string ModelId { get; init; }
        public required int Dims { get; init; }
        public required int KeyCount { get; init; }
        public required int KeyMaxId { get; init; }
        public required int Length { get; init; }
        public required int[] ChunkIds { get; init; }
        public required int[] DocumentIds { get; init; }
        public required int[] Ordinals { get; init; }
        public required float[] Vectors { get; init; }
    }

    private sealed class PassageGroup
    {
        public required int DocumentId { get; init; }
        public required List<int> MemberIds { get; init; }
        public required int RepCacheIndex { get; init; }
        public required float Cosine { get; init; }
        public required double Fused { get; init; }
    }

    public void Dispose() => _cacheLock.Dispose();
}
