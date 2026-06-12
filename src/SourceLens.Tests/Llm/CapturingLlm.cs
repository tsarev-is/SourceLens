using SourceLens.Domain;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Tests.Llm;

/// <summary>
/// Стаб LLM: запоминает промпт и ambient-контекст последнего вызова.
/// </summary>
public class CapturingLlm : AbstractLlmInferences
{
    public string LastPrompt { get; private set; } = string.Empty;

    public string? CapturedSystemPrompt { get; private set; }

    public IReadOnlyList<KnowledgeChunk>? CapturedReferenceMaterials { get; private set; }

    public string Answer { get; set; } = "  the answer [1]  ";

    public override Task<string> Question(string input)
    {
        LastPrompt = input;
        CapturedSystemPrompt = LlmContext.SystemPrompt;
        CapturedReferenceMaterials = LlmContext.ReferenceMaterials;
        return Task.FromResult(Answer);
    }
}
