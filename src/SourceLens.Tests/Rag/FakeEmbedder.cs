using SourceLens.Domain.Rag;

namespace SourceLens.Tests.Rag;

public class FakeEmbedder : IEmbedder
{
    public string ModelId { get; init; } = "fake-v1";

    public int Dimensions { get; init; } = 4;

    public int Calls { get; private set; }

    public Task<float[]> Embed(string text, EmbedKind kind, CancellationToken ct = default)
    {
        Calls++;
        var v = new float[Dimensions];
        var hash = text.GetHashCode();
        for (var i = 0; i < Dimensions; i++)
            v[i] = ((hash >> (i * 8)) & 0xff) / 256f;
        return Task.FromResult(v);
    }
}
