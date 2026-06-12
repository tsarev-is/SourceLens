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
            _ => Task.FromResult(new CliProbeResult(true, "0.42.1", "/usr/local/bin/codex")),
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
            option => Task.FromResult(new CliProbeResult(true, "3.1.0", "/usr/bin/" + option.BinaryPath)
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
    public void Probe_NotFound_DisablesModelSelection()
    {
        var getContext = CreateContextFactory();
        var llm = new CapturingLlm();
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(getContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));

        var window = new SettingsWindow(engineManager, RagWindowTests.CreateEngineOptions(),
            option => Task.FromResult(new CliProbeResult(false, string.Empty, option.BinaryPath)));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.That(window.ModelBoxFor(EngineSettings.ClaudeProvider)!.IsEnabled, Is.False);
        Assert.That(window.ModelBoxFor(EngineSettings.CodexProvider)!.IsEnabled, Is.False);
    }
}
