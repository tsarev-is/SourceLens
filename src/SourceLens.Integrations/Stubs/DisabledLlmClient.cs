using SourceLens.Domain;

namespace SourceLens.Integrations.Stubs;

/// <summary>
/// Заглушка для случая, когда движок ответов не сконфигурирован.
/// </summary>
public class DisabledLlmClient : AbstractLlmInferences
{
    public override Task<string> Question(string input)
    {
        return Task.FromResult("LLM engine is not connected. Open Settings and choose an answer engine.");
    }

    public override Task<string> SummariseChunk(string text)
    {
        return Task.FromResult(string.Empty);
    }
}
