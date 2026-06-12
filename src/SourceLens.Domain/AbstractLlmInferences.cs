using SourceLens.Domain.Prompts;

namespace SourceLens.Domain;

public abstract class AbstractLlmInferences
{
    public abstract Task<string> Question(string input);

    public async Task<string> AskWithRag(string question, string priorContext = "")
    {
        var prompt = PromptCatalog.RagAsk(question, LlmContext.ReferenceMaterials, priorContext);
        var systemPrompt = PromptCatalog.RagSystem();
        using var _ = LlmContext.WithSystemPrompt(systemPrompt);
        return (await Question(prompt))?.Trim() ?? string.Empty;
    }

    public virtual async Task<string> SummariseChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var prompt = PromptCatalog.SummariseChunk(text);
        return (await Question(prompt))?.Trim() ?? string.Empty;
    }
}
