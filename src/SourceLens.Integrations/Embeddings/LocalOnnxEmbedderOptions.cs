namespace SourceLens.Integrations.Embeddings;

public class LocalOnnxEmbedderOptions
{
    public int Dimensions { get; set; } = 384;

    public int MaxSequenceLength { get; set; } = 512;

    public int BosTokenId { get; set; } = 0;

    public int EosTokenId { get; set; } = 2;

    /// <summary>
    /// Fairseq vocab offset of XLM-RoBERTa-style models relative to raw SentencePiece ids.
    /// </summary>
    public int TokenIdOffset { get; set; } = 1;

    public string ModelIdLabel { get; set; } = "multilingual-e5-small";
}
