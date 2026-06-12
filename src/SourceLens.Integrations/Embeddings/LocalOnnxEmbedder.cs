using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SourceLens.Domain.Rag;
using SourceLens.Integrations.Models;

namespace SourceLens.Integrations.Embeddings;

public class LocalOnnxEmbedder : IEmbedder, IDisposable
{
    private const string InputIdsName = "input_ids";
    private const string AttentionMaskName = "attention_mask";
    private const string LastHiddenStateOutput = "last_hidden_state";

    private readonly LocalOnnxEmbedderOptions _options;
    private readonly Lazy<InferenceSession> _session;
    private readonly Lazy<SentencePieceTokenizer> _tokenizer;
    private bool? _hasTokenTypeIds;

    public LocalOnnxEmbedder(LocalOnnxEmbedderOptions options, ModelDownloader downloader)
    {
        if (options.Dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.Dimensions, "Dimensions must be positive");

        _options = options;

        _session = new Lazy<InferenceSession>(() =>
        {
            downloader.EnsureOnnxEmbedderAsync().GetAwaiter().GetResult();
            return new InferenceSession(ModelDownloader.GetOnnxModelPath());
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        _tokenizer = new Lazy<SentencePieceTokenizer>(() =>
        {
            downloader.EnsureOnnxEmbedderAsync().GetAwaiter().GetResult();
            using var stream = File.OpenRead(ModelDownloader.GetOnnxTokenizerPath());
            return SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ModelId => $"onnx:{_options.ModelIdLabel}:{_options.Dimensions}";

    public int Dimensions => _options.Dimensions;

    public Task<float[]> Embed(string text, EmbedKind kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var session = _session.Value;
        var tokenizer = _tokenizer.Value;
        var hasTokenTypeIds = _hasTokenTypeIds ??= session.InputMetadata.ContainsKey("token_type_ids");

        var prefix = kind == EmbedKind.Query ? "query: " : "passage: ";
        var rawIds = tokenizer.EncodeToIds(prefix + (text ?? string.Empty));
        var ids = BuildIdsWithSpecials(rawIds);
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);

        var inputIdsTensor = new DenseTensor<long>(ids, new[] { 1, ids.Length });
        var attentionMaskTensor = new DenseTensor<long>(mask, new[] { 1, ids.Length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputIdsName, inputIdsTensor),
            NamedOnnxValue.CreateFromTensor(AttentionMaskName, attentionMaskTensor),
        };
        if (hasTokenTypeIds)
        {
            var zeros = new long[ids.Length];
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(zeros, new[] { 1, ids.Length })));
        }

        using var outputs = session.Run(inputs);
        var hidden = (outputs.Count == 1
            ? outputs[0]
            : outputs.First(o => o.Name == LastHiddenStateOutput)).AsTensor<float>();

        var pooled = MeanPool(hidden, mask);
        L2Normalize(pooled);
        if (pooled.Length != _options.Dimensions)
            throw new InvalidOperationException($"Pooled vector has {pooled.Length} dims, expected {_options.Dimensions}");
        return Task.FromResult(pooled);
    }

    private long[] BuildIdsWithSpecials(IReadOnlyList<int> rawIds)
    {
        var max = Math.Max(2, _options.MaxSequenceLength);
        var bodyCapacity = max - 2;
        var bodyCount = Math.Min(rawIds.Count, bodyCapacity);
        var ids = new long[bodyCount + 2];
        ids[0] = _options.BosTokenId;
        for (var i = 0; i < bodyCount; i++)
            ids[i + 1] = ToModelTokenId(rawIds[i]);
        ids[bodyCount + 1] = _options.EosTokenId;
        return ids;
    }

    /// <summary>
    /// Maps a raw SentencePiece id to the XLM-RoBERTa (fairseq) vocabulary used by multilingual-e5:
    /// the model vocab prepends &lt;s&gt;/&lt;pad&gt;/&lt;/s&gt;/&lt;unk&gt;, so regular pieces are shifted by +1
    /// and the SentencePiece &lt;unk&gt; (0) becomes 3.
    /// </summary>
    private long ToModelTokenId(int rawId)
    {
        return rawId == 0 ? 3L : rawId + (long)_options.TokenIdOffset;
    }

    private float[] MeanPool(Tensor<float> hidden, long[] mask)
    {
        var seqLen = hidden.Dimensions[1];
        var dim = hidden.Dimensions[2];
        var sum = new float[dim];
        var tokensCounted = 0L;
        for (var t = 0; t < seqLen; t++)
        {
            if (mask[t] == 0)
                continue;
            tokensCounted++;
            for (var d = 0; d < dim; d++)
                sum[d] += hidden[0, t, d];
        }
        if (tokensCounted == 0)
            return sum;
        for (var d = 0; d < dim; d++)
            sum[d] /= tokensCounted;
        return sum;
    }

    private static void L2Normalize(float[] vector)
    {
        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
            sumSq += vector[i] * vector[i];
        var norm = (float)Math.Sqrt(sumSq);
        if (norm <= 1e-12f)
            return;
        for (var i = 0; i < vector.Length; i++)
            vector[i] /= norm;
    }

    public void Dispose()
    {
        if (_session.IsValueCreated)
            _session.Value.Dispose();
    }
}
