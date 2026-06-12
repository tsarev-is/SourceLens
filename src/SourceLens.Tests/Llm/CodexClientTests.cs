using NUnit.Framework;
using SourceLens.Integrations.Codex;

namespace SourceLens.Tests.Llm;

[Platform("Linux")]
public class CodexClientTests
{
    [Test]
    public async Task Question_PassesSkipGitRepoCheckFlag()
    {
        // /bin/echo prints its argument list back, exposing the exact CLI invocation.
        var client = new CodexClient("/bin/echo");

        var result = await client.Question("ping");

        Assert.That(result, Does.StartWith("exec --skip-git-repo-check"),
            "Codex must be invoked with --skip-git-repo-check: the app runs from its install folder, " +
            "which is not a trusted git directory, and codex exec refuses to start there otherwise");
        Assert.That(result, Does.EndWith("ping"));
    }
}
