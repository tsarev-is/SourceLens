using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain.Rag;

public interface IKnowledgeRetriever
{
    /// <summary>
    /// Возвращает до <paramref name="topK"/> наиболее релевантных чанков. <paramref name="scope"/>
    /// ограничивает поиск выбранными книгами (null — вся библиотека).
    /// </summary>
    Task<KnowledgeChunk[]> Retrieve(string query, int topK, RetrievalScope? scope = null, CancellationToken ct = default);
}
