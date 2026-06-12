using NUnit.Framework;
using SourceLens.Domain;
using SourceLens.Integrations.Cli;

namespace SourceLens.Tests.Llm;

public class CliModelCatalogTests
{
    private const string ClaudeHelp = """
          --mcp-debug                           [DEPRECATED. Use --debug instead] Enable
                                                MCP debug mode (shows MCP server errors)
          --model <model>                       Model for the current session. Provide
                                                an alias for the latest model (e.g.
                                                'fable', 'opus', or 'sonnet') or a
                                                model's full name (e.g.
                                                'claude-fable-5').
          -n, --name <name>                     Set a display name for this session
                                                (shown in the prompt box, /resume
                                                picker, and terminal title)
        """;

    private const string CodexCatalog = """
        {"models":[
          {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list","priority":9},
          {"slug":"gpt-5.4","display_name":"GPT-5.4","visibility":"list","priority":16},
          {"slug":"gpt-5.4-mini","display_name":"GPT-5.4-Mini","visibility":"list","priority":23},
          {"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide","priority":43}
        ]}
        """;

    [Test]
    public void ParseClaudeHelp_ExtractsAliasesWithoutFullModelNames()
    {
        var models = CliModelCatalog.ParseClaudeHelp(ClaudeHelp);

        Assert.That(models, Is.EqualTo(new[] { "fable", "opus", "sonnet" }));
    }

    [Test]
    public void ParseClaudeHelp_NoModelOption_ReturnsEmpty()
    {
        Assert.That(CliModelCatalog.ParseClaudeHelp("Usage: claude [options]"), Is.Empty);
        Assert.That(CliModelCatalog.ParseClaudeHelp(string.Empty), Is.Empty);
    }

    [Test]
    public void ParseCodexCatalog_ListedModelsOrderedByPriority()
    {
        var models = CliModelCatalog.ParseCodexCatalog(CodexCatalog);

        Assert.That(models, Is.EqualTo(new[] { "gpt-5.5", "gpt-5.4", "gpt-5.4-mini" }));
    }

    [Test]
    public void ParseCodexCatalog_EmptyOrMalformed_ReturnsEmpty()
    {
        Assert.That(CliModelCatalog.ParseCodexCatalog(string.Empty), Is.Empty);
        Assert.That(CliModelCatalog.ParseCodexCatalog("{}"), Is.Empty);
    }

    [Test]
    public async Task List_MissingBinary_ReturnsEmpty()
    {
        var models = await CliModelCatalog.List(EngineSettings.ClaudeProvider, "definitely-missing-binary-xyz");

        Assert.That(models, Is.Empty);
    }
}

[Explicit("Runs the real claude/codex CLI binaries installed on this machine.")]
[Category("Integration")]
public class CliModelCatalogIntegrationTests
{
    [Test]
    public async Task List_RealClaudeCli_ReturnsAliases()
    {
        var models = await CliModelCatalog.List(EngineSettings.ClaudeProvider, "/home/user/.local/bin/claude");

        Assert.That(models, Is.Not.Empty, "claude --help must expose --model aliases");
        TestContext.Out.WriteLine($"claude models: {string.Join(", ", models)}");
    }

    [Test]
    public async Task List_RealCodexCli_ReturnsCatalogSlugs()
    {
        var probe = await CliEngineProbe.Probe("codex");
        if (!probe.Found)
            Assert.Inconclusive("codex CLI is not on PATH in this test environment (installed via nvm; acceptable)");

        var models = await CliModelCatalog.List(EngineSettings.CodexProvider, probe.ResolvedPath);

        Assert.That(models, Is.Not.Empty, "codex debug models must return a non-empty catalog");
        TestContext.Out.WriteLine($"codex models: {string.Join(", ", models)}");
    }
}
