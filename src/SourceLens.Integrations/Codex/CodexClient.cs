using SourceLens.Domain;
using SourceLens.Integrations.Cli;

namespace SourceLens.Integrations.Codex;

public class CodexClient : AbstractLlmInferences
{
    private readonly string[] _extraArgs;
    private readonly string? _model;
    private readonly CliPromptRunner _runner;

    public CodexClient(string binaryPath, string[]? extraArgs = null, string? model = null, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
            throw new ArgumentException("Codex binary path must be provided", nameof(binaryPath));

        _extraArgs = extraArgs ?? Array.Empty<string>();
        _model = model;
        _runner = new CliPromptRunner(
            CliBinaryResolver.Resolve(binaryPath),
            workingDirectory,
            timeout ?? TimeSpan.FromSeconds(300),
            "Codex");
    }

    public override Task<string> Question(string input)
    {
        var systemPrompt = LlmContext.SystemPrompt;
        var prompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? input
            : $"[SYSTEM INSTRUCTIONS]\n{systemPrompt}\n[/SYSTEM INSTRUCTIONS]\n\n{input}";

        return _runner.RunAsync((startInfo, useStdin) =>
        {
            startInfo.ArgumentList.Add("exec");
            // Codex refuses to run outside a trusted (git) directory; the app's working
            // directory is the install folder, so the check must be skipped explicitly.
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            foreach (var arg in _extraArgs)
                startInfo.ArgumentList.Add(arg);
            if (!string.IsNullOrWhiteSpace(_model))
            {
                startInfo.ArgumentList.Add("-m");
                startInfo.ArgumentList.Add(_model);
            }
            startInfo.ArgumentList.Add(useStdin ? "-" : prompt);
        }, prompt);
    }
}
