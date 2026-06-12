namespace SourceLens.Domain.Rag;

public interface IEmbedder
{
    string ModelId { get; }

    int Dimensions { get; }

    Task<float[]> Embed(string text, EmbedKind kind, CancellationToken ct = default);
}
