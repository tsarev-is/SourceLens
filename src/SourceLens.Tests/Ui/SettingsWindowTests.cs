using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain;
using SourceLens.Domain.Entities;
using SourceLens.Integrations.Cli;
using SourceLens.Tests.Llm;
using SourceLens.Windows;

namespace SourceLens.Tests.Ui;

public class SettingsWindowTests
{
    private static Func<SourceLensContext> CreateContextFactory()
    {
        var builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseInMemoryDatabase("SettingsWindowTests_" + Guid.NewGuid());
        return () =>
        {
            var ctx = new SourceLensContext(builder.Options);
            ctx.Database.EnsureCreated();
            return ctx;
        };
    }

    [AvaloniaTest]
    public void SelectProvider_SwitchesEngineAndPersistsChoice()
    {
        var getContext = CreateContextFactory();
        var llm = new CapturingLlm();
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(getContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));
        var changedCalls = 0;

        var window = new SettingsWindow(engineManager, RagWindowTests.CreateEngineOptions(),
            (_, _) => Task.FromResult(new CliProbeResult(true, "0.42.1", "/usr/local/bin/codex")),
            () => changedCalls++);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.That(engineManager.Provider, Is.EqualTo(EngineSettings.ClaudeProvider));

        window.SelectProvider(EngineSettings.CodexProvider);
        Dispatcher.UIThread.RunJobs();

        Assert.That(engineManager.Provider, Is.EqualTo(EngineSettings.CodexProvider));
        Assert.That(engineManager.Model, Is.EqualTo("gpt-5-codex"));
        Assert.That(engineManager.EngineLabel, Is.EqualTo("Codex · gpt-5-codex"));
        Assert.That(changedCalls, Is.GreaterThanOrEqualTo(1));

        using var ctx = getContext();
        Assert.That(ctx.GetSetting(EngineSettings.ProviderKey), Is.EqualTo(EngineSettings.CodexProvider));
        Assert.That(ctx.GetSetting(EngineSettings.CodexModelKey), Is.EqualTo("gpt-5-codex"));
    }

    [AvaloniaTest]
    public void Probe_CliModels_ReplaceConfiguredListAndResetUnavailableModel()
    {
        var getContext = CreateContextFactory();
        var llm = new CapturingLlm();
        // Сохранённая модель "haiku" есть в конфигурации, но CLI её больше не позволяет выбрать.
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(getContext, EngineSettings.ClaudeProvider, "haiku", "gpt-5-codex"));
        var discovered = new[] { "fable", "opus", "sonnet" };

        var window = new SettingsWindow(engineManager, RagWindowTests.CreateEngineOptions(),
            (option, binaryPath) => Task.FromResult(new CliProbeResult(true, "3.1.0", "/usr/bin/" + binaryPath)
            {
                Models = option.Provider == EngineSettings.ClaudeProvider ? discovered : Array.Empty<string>(),
            }));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var claudeBox = window.ModelBoxFor(EngineSettings.ClaudeProvider)!;
        Assert.That(claudeBox.IsEnabled, Is.True);
        Assert.That(claudeBox.ItemsSource, Is.EqualTo(discovered));
        Assert.That(claudeBox.SelectedItem, Is.EqualTo("sonnet"), "unavailable model must fall back to the engine default");
        Assert.That(engineManager.Model, Is.EqualTo("sonnet"));

        // Codex CLI список не сообщил — в списке остаётся только текущая модель.
        var codexBox = window.ModelBoxFor(EngineSettings.CodexProvider)!;
        Assert.That(codexBox.IsEnabled, Is.True);
        Assert.That(codexBox.ItemsSource, Is.EqualTo(new[] { "gpt-5-codex" }));

        using var ctx = getContext();
        Assert.That(ctx.GetSetting(EngineSettings.ClaudeModelKey), Is.EqualTo("sonnet"));
    }

    [AvaloniaTest]
    public void EditBinaryPath_PersistsOverrideAndReprobesWithNewPath()
    {
        var getContext = CreateContextFactory();
        var llm = new CapturingLlm();
        var factoryCalls = 0;
        var probedPaths = new List<string>();
        var engineManager = new AnswerEngineManager((_, _) => { factoryCalls++; return llm; },
            new EngineSettings(getContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));

        var window = new SettingsWindow(engineManager, RagWindowTests.CreateEngineOptions(),
            (option, binaryPath) =>
            {
                probedPaths.Add($"{option.Provider}:{binaryPath}");
                return Task.FromResult(new CliProbeResult(true, "1.0.0", binaryPath));
            });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var pathBox = window.BinaryPathBoxFor(EngineSettings.ClaudeProvider)!;
        Assert.That(pathBox.Text, Is.EqualTo("claude"), "initial value comes from the engine option");

        factoryCalls = 0;
        pathBox.Text = "/opt/claude/bin/claude";
        window.CommitBinaryPathFor(EngineSettings.ClaudeProvider);
        Dispatcher.UIThread.RunJobs();

        Assert.That(probedPaths, Does.Contain("Claude:/opt/claude/bin/claude"), "probe must re-run with the new path");
        Assert.That(factoryCalls, Is.EqualTo(1), "active engine client must be recreated");
        using var ctx = getContext();
        Assert.That(ctx.GetSetting(EngineSettings.ClaudeBinaryPathKey), Is.EqualTo("/opt/claude/bin/claude"));
    }

    [AvaloniaTest]
    public void EditBinaryPath_EmptyInput_RevertsToCurrentPath()
    {
        var getContext = CreateContextFactory();
        var llm = new CapturingLlm();
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(getContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));

        var window = new SettingsWindow(engineManager, RagWindowTests.CreateEngineOptions(),
            (_, binaryPath) => Task.FromResult(new CliProbeResult(true, "1.0.0", binaryPath)));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var pathBox = window.BinaryPathBoxFor(EngineSettings.ClaudeProvider)!;
        pathBox.Text = "   ";
        window.CommitBinaryPathFor(EngineSettings.ClaudeProvider);
        Dispatcher.UIThread.RunJobs();

        Assert.That(pathBox.Text, Is.EqualTo("claude"));
        using var ctx = getContext();
        Assert.That(ctx.GetSetting(EngineSettings.ClaudeBinaryPathKey), Is.Null, "empty path must not be persisted");
    }

    [AvaloniaTest]
    public void Probe_NotFound_DisablesModelSelection()
    {
        var getContext = CreateContextFactory();
        var llm = new CapturingLlm();
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(getContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));

        var window = new SettingsWindow(engineManager, RagWindowTests.CreateEngineOptions(),
            (_, binaryPath) => Task.FromResult(new CliProbeResult(false, string.Empty, binaryPath)));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.That(window.ModelBoxFor(EngineSettings.ClaudeProvider)!.IsEnabled, Is.False);
        Assert.That(window.ModelBoxFor(EngineSettings.CodexProvider)!.IsEnabled, Is.False);
    }
}
