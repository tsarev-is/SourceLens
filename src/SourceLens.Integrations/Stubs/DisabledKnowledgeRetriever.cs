using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Integrations.Stubs;

/// <summary>
/// Used when Rag.Enabled=false: retrieval always yields no chunks.
/// </summary>
public class DisabledKnowledgeRetriever : IKnowledgeRetriever
{
    public Task<KnowledgeChunk[]> Retrieve(string query, int topK, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Array.Empty<KnowledgeChunk>());
    }
}
