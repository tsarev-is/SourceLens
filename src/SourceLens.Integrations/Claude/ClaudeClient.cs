using SourceLens.Domain;
using SourceLens.Integrations.Cli;

namespace SourceLens.Integrations.Claude;

public class ClaudeClient : AbstractLlmInferences
{
    private readonly string[] _extraArgs;
    private readonly string? _model;
    private readonly CliPromptRunner _runner;

    public ClaudeClient(string binaryPath, string[]? extraArgs = null, string? model = null, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
            throw new ArgumentException("Claude binary path must be provided", nameof(binaryPath));

        _extraArgs = extraArgs ?? new[] { "-p", "--output-format", "text" };
        _model = model;
        _runner = new CliPromptRunner(
            CliBinaryResolver.Resolve(binaryPath),
            workingDirectory,
            timeout ?? TimeSpan.FromSeconds(300),
            "Claude");
    }

    public override Task<string> Question(string input)
    {
        return _runner.RunAsync((startInfo, useStdin) =>
        {
            foreach (var arg in _extraArgs)
                startInfo.ArgumentList.Add(arg);
            if (!string.IsNullOrWhiteSpace(_model))
            {
                startInfo.ArgumentList.Add("--model");
                startInfo.ArgumentList.Add(_model);
            }
            var systemPrompt = LlmContext.SystemPrompt;
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                startInfo.ArgumentList.Add("--append-system-prompt");
                startInfo.ArgumentList.Add(systemPrompt);
            }
            if (!useStdin)
                startInfo.ArgumentList.Add(input);
        }, input);
    }
}
