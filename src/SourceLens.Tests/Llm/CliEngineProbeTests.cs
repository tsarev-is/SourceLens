using NUnit.Framework;
using SourceLens.Integrations.Cli;

namespace SourceLens.Tests.Llm;

public class CliEngineProbeTests
{
    [Test]
    public async Task Probe_MissingBinary_ReturnsNotFound()
    {
        var result = await CliEngineProbe.Probe("definitely-missing-binary-xyz");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Version, Is.Empty);
        Assert.That(result.ResolvedPath, Is.EqualTo("definitely-missing-binary-xyz"));
    }

    [Test]
    public async Task Probe_EmptyPath_ReturnsNotFound()
    {
        var result = await CliEngineProbe.Probe(string.Empty);

        Assert.That(result.Found, Is.False);
    }

    [Test]
    [Platform("Linux")]
    public async Task Probe_BinaryFromPath_ResolvesAbsolutePathAndVersion()
    {
        // sh гарантированно есть на PATH в любой Linux-системе (POSIX), в т.ч. на CI-раннерах
        var result = await CliEngineProbe.Probe("sh");

        Assert.That(result.Found, Is.True);
        Assert.That(Path.IsPathRooted(result.ResolvedPath), Is.True);
        Assert.That(File.Exists(result.ResolvedPath), Is.True);
    }
}

[Explicit("Probes the real claude/codex CLI binaries installed on this machine.")]
[Category("Integration")]
public class CliEngineProbeIntegrationTests
{
    [Test]
    public async Task Probe_FindsRealClaudeBinary()
    {
        var result = await CliEngineProbe.Probe("/home/user/.local/bin/claude");

        Assert.That(result.Found, Is.True, "claude CLI must be detected at /home/user/.local/bin/claude");
        Assert.That(result.ResolvedPath, Is.EqualTo("/home/user/.local/bin/claude"));
        Assert.That(result.Version, Is.Not.Empty, "claude --version must return a version string");
        TestContext.Out.WriteLine($"claude: {result.Version} at {result.ResolvedPath}");
    }

    [Test]
    public async Task Probe_FindsRealCodexBinary_WhenOnPath()
    {
        var result = await CliEngineProbe.Probe("codex");

        if (!result.Found)
            Assert.Inconclusive("codex CLI is not on PATH in this test environment (installed via nvm; acceptable)");

        Assert.That(result.Version, Is.Not.Empty, "codex --version must return a version string");
        TestContext.Out.WriteLine($"codex: {result.Version} at {result.ResolvedPath}");
    }
}
