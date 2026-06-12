using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain;
using SourceLens.Domain.Entities;

namespace SourceLens.Tests.Managers;

public class AnswerEngineManagerTests
{
    private DbContextOptionsBuilder<SourceLensContext> _builder = Global.GetBuilder();

    [TearDown]
    public void TearDown()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.Database.EnsureDeleted();
    }

    private SourceLensContext GetContext() => Global.CreateContext(_builder);

    private sealed class FakeEngine : AbstractLlmInferences
    {
        public override Task<string> Question(string input) => Task.FromResult(string.Empty);
    }

    [Test]
    public void Constructor_NoSavedSelection_UsesDefaultsFromConfig()
    {
        var calls = new List<(string Provider, string Model)>();
        var settings = new EngineSettings(GetContext, "Claude", "sonnet", "gpt-5-codex");

        var manager = new AnswerEngineManager((p, m) => { calls.Add((p, m)); return new FakeEngine(); }, settings);

        Assert.That(calls, Is.EqualTo(new[] { ("Claude", "sonnet") }));
        Assert.That(manager.Provider, Is.EqualTo("Claude"));
        Assert.That(manager.Model, Is.EqualTo("sonnet"));
        Assert.That(manager.EngineLabel, Is.EqualTo("Claude · sonnet"));
        Assert.That(manager.Current, Is.Not.Null);
    }

    [Test]
    public void Switch_RecreatesClient_AndPersistsSelection()
    {
        var calls = new List<(string Provider, string Model)>();
        Func<string, string, AbstractLlmInferences> factory = (p, m) => { calls.Add((p, m)); return new FakeEngine(); };
        var manager = new AnswerEngineManager(factory, new EngineSettings(GetContext, "Claude", "sonnet", "gpt-5-codex"));
        var before = manager.Current;

        var after = manager.Switch("Codex", "gpt-5.1-codex");

        Assert.That(after, Is.SameAs(manager.Current));
        Assert.That(manager.Current, Is.Not.SameAs(before), "Switch must recreate the client");
        Assert.That(calls[^1], Is.EqualTo(("Codex", "gpt-5.1-codex")));
        Assert.That(manager.EngineLabel, Is.EqualTo("Codex · gpt-5.1-codex"));

        using (var ctx = GetContext())
        {
            Assert.That(ctx.GetSetting("engine.provider"), Is.EqualTo("Codex"));
            Assert.That(ctx.GetSetting("engine.codex.model"), Is.EqualTo("gpt-5.1-codex"));
            Assert.That(ctx.GetSetting("engine.claude.model"), Is.Null, "Claude model setting is untouched");
        }

        // Новый менеджер (новый запуск приложения) восстанавливает сохранённый выбор поверх дефолтов.
        var restored = new AnswerEngineManager(factory, new EngineSettings(GetContext, "Claude", "sonnet", "default-codex"));
        Assert.That(restored.Provider, Is.EqualTo("Codex"));
        Assert.That(restored.Model, Is.EqualTo("gpt-5.1-codex"));
    }

    [Test]
    public void SetBinaryPath_ActiveProvider_PersistsAndRecreatesClient()
    {
        var calls = new List<(string Provider, string Model)>();
        var settings = new EngineSettings(GetContext, "Claude", "sonnet", "gpt-5-codex", "claude", "codex");
        var manager = new AnswerEngineManager((p, m) => { calls.Add((p, m)); return new FakeEngine(); }, settings);
        var before = manager.Current;

        manager.SetBinaryPath("Claude", "/opt/claude/bin/claude");

        Assert.That(manager.Current, Is.Not.SameAs(before), "active engine client must be recreated with the new path");
        Assert.That(calls[^1], Is.EqualTo(("Claude", "sonnet")));
        Assert.That(manager.GetBinaryPath("Claude"), Is.EqualTo("/opt/claude/bin/claude"));
        using var ctx = GetContext();
        Assert.That(ctx.GetSetting(EngineSettings.ClaudeBinaryPathKey), Is.EqualTo("/opt/claude/bin/claude"));
    }

    [Test]
    public void SetBinaryPath_InactiveProvider_PersistsWithoutRecreatingClient()
    {
        var settings = new EngineSettings(GetContext, "Claude", "sonnet", "gpt-5-codex", "claude", "codex");
        var manager = new AnswerEngineManager((_, _) => new FakeEngine(), settings);
        var before = manager.Current;

        manager.SetBinaryPath("Codex", "/opt/codex/bin/codex");

        Assert.That(manager.Current, Is.SameAs(before), "inactive engine change must not touch the current client");
        Assert.That(manager.GetBinaryPath("Codex"), Is.EqualTo("/opt/codex/bin/codex"));
        Assert.That(manager.GetBinaryPath("Claude"), Is.EqualTo("claude"), "no override saved — config default stays");
    }

    [Test]
    public void EngineLabel_NoProvider_IsNotConnected()
    {
        var manager = new AnswerEngineManager((_, _) => new FakeEngine(), new EngineSettings(GetContext));

        Assert.That(manager.Provider, Is.Empty);
        Assert.That(manager.EngineLabel, Is.EqualTo("Not connected"));
    }

    [Test]
    public void EngineLabel_ProviderWithoutModel_ShowsProviderOnly()
    {
        var manager = new AnswerEngineManager((_, _) => new FakeEngine(), new EngineSettings(GetContext, "Claude"));

        Assert.That(manager.EngineLabel, Is.EqualTo("Claude"));
    }
}
