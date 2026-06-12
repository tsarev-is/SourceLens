using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain;

/// <summary>
/// Ambient контекст для LLM-вызовов. Передаёт инструкции уровня системного промпта
/// и материалы извлечённого контекста (RAG) от слоя сценария к конкретному клиенту,
/// который применяет их нативным для каждой модели способом, без изменения сигнатур.
/// </summary>
public static class LlmContext
{
    private static readonly AsyncLocal<string?> CurrentSystemPrompt = new();
    private static readonly AsyncLocal<IReadOnlyList<KnowledgeChunk>?> CurrentReferenceMaterials = new();

    public static string? SystemPrompt => CurrentSystemPrompt.Value;

    public static IReadOnlyList<KnowledgeChunk>? ReferenceMaterials => CurrentReferenceMaterials.Value;

    public static IDisposable WithSystemPrompt(string? systemPrompt)
    {
        var previous = CurrentSystemPrompt.Value;
        CurrentSystemPrompt.Value = systemPrompt;
        return new Scope(() => CurrentSystemPrompt.Value = previous);
    }

    public static IDisposable WithReferenceMaterials(IReadOnlyList<KnowledgeChunk>? referenceMaterials)
    {
        var previous = CurrentReferenceMaterials.Value;
        CurrentReferenceMaterials.Value = referenceMaterials;
        return new Scope(() => CurrentReferenceMaterials.Value = previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _restore;

        public Scope(Action restore) => _restore = restore;

        public void Dispose() => _restore();
    }
}
