using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain.Rag;

public interface IKnowledgeRetriever
{
    Task<KnowledgeChunk[]> Retrieve(string query, int topK, CancellationToken ct = default);
}
