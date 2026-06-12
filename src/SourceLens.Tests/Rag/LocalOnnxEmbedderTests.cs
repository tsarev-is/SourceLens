using System.Numerics.Tensors;
using NUnit.Framework;
using SourceLens.Domain.Rag;
using SourceLens.Integrations.Embeddings;
using SourceLens.Integrations.Models;

namespace SourceLens.Tests.Rag;

[Explicit("Downloads multilingual-e5-small ONNX + sentencepiece files into ./models on first run (network access required).")]
[Category("Integration")]
public class LocalOnnxEmbedderTests
{
    private LocalOnnxEmbedder _embedder = null!;

    [SetUp]
    public void SetUp()
    {
        _embedder = new LocalOnnxEmbedder(new LocalOnnxEmbedderOptions
        {
            Dimensions = 384,
        }, new ModelDownloader());
    }

    [TearDown]
    public void TearDown()
    {
        _embedder?.Dispose();
    }

    [Test]
    public async Task Embed_ReturnsExpectedDimensionsAndUnitNorm()
    {
        var v = await _embedder.Embed("Здравствуй, мир", EmbedKind.Query);

        Assert.That(v, Has.Length.EqualTo(384));
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        Assert.That(norm, Is.EqualTo(1.0).Within(1e-3), "Result should be L2-normalized");
    }

    [Test]
    public async Task Embed_SimilarPhrasesScoreHigherThanUnrelated()
    {
        var query = await _embedder.Embed("How do plants make their food?", EmbedKind.Query);
        var close = await _embedder.Embed("Photosynthesis lets plants convert sunlight into chemical energy.", EmbedKind.Passage);
        var far = await _embedder.Embed("The stock market closed lower on Tuesday amid inflation fears.", EmbedKind.Passage);

        var cosClose = TensorPrimitives.CosineSimilarity(query, close);
        var cosFar = TensorPrimitives.CosineSimilarity(query, far);

        Assert.That(cosClose, Is.GreaterThan(cosFar),
            $"Expected related passage to score higher: close={cosClose}, far={cosFar}");
    }

    [Test]
    public async Task Embed_QueryAndPassagePrefixesYieldDifferentVectors()
    {
        var q = await _embedder.Embed("текст", EmbedKind.Query);
        var p = await _embedder.Embed("текст", EmbedKind.Passage);

        Assert.That(q, Is.Not.EqualTo(p));
    }
}
