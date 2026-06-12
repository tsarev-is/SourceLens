using System.Diagnostics;
using NUnit.Framework;
using SourceLens.Integrations.Cli;

namespace SourceLens.Tests.Llm;

[Platform("Linux")]
public class CliPromptRunnerTests
{
    [Test]
    public async Task ShortPrompt_IsPassedAsArgument_AndReturnedWhole()
    {
        var prompt = "short prompt with спецсимволы and \"quotes\"";
        var runner = new CliPromptRunner("/bin/echo", null, TimeSpan.FromSeconds(30), "Echo");
        bool? capturedUseStdin = null;

        var result = await runner.RunAsync((startInfo, useStdin) =>
        {
            capturedUseStdin = useStdin;
            if (!useStdin)
                startInfo.ArgumentList.Add(prompt);
        }, prompt);

        Assert.That(capturedUseStdin, Is.False, "Prompt of <= 8000 chars must go as an argument");
        Assert.That(result, Is.EqualTo(prompt));
    }

    [Test]
    public async Task LongPrompt_GoesThroughStdin_AndIsReturnedWhole()
    {
        var prompt = string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"line-{i:D5}"));
        Assert.That(prompt.Length, Is.GreaterThan(8000), "Test prompt must exceed the argument limit");

        var runner = new CliPromptRunner("/bin/cat", null, TimeSpan.FromSeconds(30), "Cat");
        bool? capturedUseStdin = null;

        var result = await runner.RunAsync((startInfo, useStdin) =>
        {
            capturedUseStdin = useStdin;
            if (!useStdin)
                startInfo.ArgumentList.Add(prompt);
        }, prompt);

        Assert.That(capturedUseStdin, Is.True, "Prompt of > 8000 chars must go through stdin");
        Assert.That(result, Is.EqualTo(prompt));
    }

    [Test]
    public void Timeout_KillsProcessAndThrowsTimeoutException()
    {
        var runner = new CliPromptRunner("/bin/sleep", null, TimeSpan.FromSeconds(1), "Sleep");
        var stopwatch = Stopwatch.StartNew();

        var ex = Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(
            (startInfo, _) => startInfo.ArgumentList.Add("60"),
            string.Empty));

        stopwatch.Stop();
        Assert.That(ex!.Message, Does.Contain("Sleep"));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)),
            "Process must be killed promptly on timeout instead of waiting for natural exit");
    }

    [Test]
    public void NonZeroExitCode_ThrowsWithStderr()
    {
        var runner = new CliPromptRunner("/bin/cat", null, TimeSpan.FromSeconds(30), "Cat");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            (startInfo, _) => startInfo.ArgumentList.Add("/nonexistent-file-for-test"),
            string.Empty));

        Assert.That(ex!.Message, Does.Contain("Cat exited with code"));
        Assert.That(ex.Message, Does.Contain("nonexistent-file-for-test"));
    }
}
