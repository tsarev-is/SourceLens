using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Tests.Managers;

public class StubRetriever : IKnowledgeRetriever
{
    public KnowledgeChunk[] Chunks { get; set; } = Array.Empty<KnowledgeChunk>();

    public List<(string Query, int TopK)> Calls { get; } = new();

    public List<RetrievalScope?> Scopes { get; } = new();

    public Task<KnowledgeChunk[]> Retrieve(string query, int topK, RetrievalScope? scope = null, CancellationToken ct = default)
    {
        Calls.Add((query, topK));
        Scopes.Add(scope);
        return Task.FromResult(Chunks);
    }
}
