using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Configuration;
using SourceLens.Domain;
using SourceLens.Domain.Audio;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;
using SourceLens.Integrations.Cli;
using SourceLens.Tests.Llm;
using SourceLens.Tests.Managers;
using SourceLens.Tests.Voice;
using SourceLens.Windows;

namespace SourceLens.Tests.Ui;

public class RagWindowTests
{
    private sealed class Harness
    {
        public required RagWindow Window { get; init; }

        public required CapturingLlm Llm { get; init; }

        public required RagDialogManager DialogManager { get; init; }

        public ManualLlm? ManualLlm { get; init; }
    }

    private static Harness CreateWindow(SourceLibraryManager? libraryManager = null)
    {
        var builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseInMemoryDatabase("RagWindowTests_" + Guid.NewGuid());

        SourceLensContext GetContext()
        {
            var ctx = new SourceLensContext(builder.Options);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        var llm = new CapturingLlm { Answer = "The stub answer [1]." };
        var retriever = new StubRetriever
        {
            Chunks = new[]
            {
                new KnowledgeChunk
                {
                    Text = "Dense retrieval uses learned embeddings.",
                    SourceTitle = "IR Book",
                    SourceLocation = "p. 3",
                    Score = 0.87f,
                },
            },
        };
        var dialogManager = new RagDialogManager(GetContext, () => llm, retriever,
            new RetrievalOptions { TopK = 5, MinQueryLength = 1 }, new RagDialogOptions());
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(GetContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));

        var window = new RagWindow(
            engineManager,
            new TranscriptFactory(new StubTranscriptor()),
            new UiRecorder(new StubRecorder()),
            dialogManager,
            libraryManager,
            (_, _) => Task.FromResult(new CliProbeResult(true, "1.0.0", "/usr/bin/stub")),
            CreateEngineOptions());

        return new Harness { Window = window, Llm = llm, DialogManager = dialogManager };
    }

    internal static EngineOption[] CreateEngineOptions()
    {
        return new[]
        {
            new EngineOption
            {
                Provider = EngineSettings.CodexProvider,
                BinaryPath = "codex",
                DefaultModel = "gpt-5-codex",
            },
            new EngineOption
            {
                Provider = EngineSettings.ClaudeProvider,
                BinaryPath = "claude",
                DefaultModel = "sonnet",
            },
        };
    }

    private static Border[] ExchangeRows(RagWindow window)
    {
        return window.HistoryPanel.Children.OfType<Border>().Where(b => b.Tag is int).ToArray();
    }

    [AvaloniaTest]
    public void SourcesButton_HiddenWithoutLibraryManager_VisibleWithIt()
    {
        var withoutManager = CreateWindow();
        withoutManager.Window.Show();
        Assert.That(withoutManager.Window.SourcesButton.IsVisible, Is.False);

        var booksFolder = Path.Combine(Path.GetTempPath(), "books_" + Guid.NewGuid());
        try
        {
            var builder = new DbContextOptionsBuilder<SourceLensContext>()
                .UseInMemoryDatabase("RagWindowTests_" + Guid.NewGuid());
            var manager = new SourceLibraryManager(
                () => Global.CreateContext(builder), new StubIngestor(), booksFolder);
            var withManager = CreateWindow(manager);
            withManager.Window.Show();
            Assert.That(withManager.Window.SourcesButton.IsVisible, Is.True);
        }
        finally
        {
            if (Directory.Exists(booksFolder))
                Directory.Delete(booksFolder, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task Send_PutsAnswerInAnswerPaneAndAddsHistoryRow()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "What is dense retrieval?";
        Assert.That(harness.Window.SendButton.IsEnabled, Is.True);

        await harness.Window.SendAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.That(harness.Window.AnswerBlock.Inlines!.Text, Is.EqualTo("The stub answer [1]."));
        Assert.That(harness.Window.AnswerBlock.IsVisible, Is.True);
        Assert.That(harness.Window.AnswerPlaceholder.IsVisible, Is.False);
        Assert.That(harness.Window.SourcesCountText.Text, Is.EqualTo("top-1"));
        Assert.That(harness.Window.PromptBox.Text, Is.EqualTo("What is dense retrieval?"),
            "вопрос остаётся в поле — очистка workbench только по New dialog");
        Assert.That(harness.Window.StatusText.Text, Is.EqualTo("Ready"));
        Assert.That(ExchangeRows(harness.Window), Has.Length.EqualTo(1));
        Assert.That(harness.Window.ContextValueText.Text, Is.EqualTo("1 exchange"));
        // Чанки ушли в LLM через ambient-контекст.
        Assert.That(harness.Llm.CapturedReferenceMaterials, Is.Not.Null);
        Assert.That(harness.Llm.CapturedReferenceMaterials, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public void CtrlEnter_SendsPrompt()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "What is dense retrieval?";
        harness.Window.PromptBox.Focus();
        harness.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.That(harness.Window.AnswerBlock.Inlines!.Text, Is.EqualTo("The stub answer [1]."));
        Assert.That(harness.Window.PromptBox.Text, Is.EqualTo("What is dense retrieval?"));
        Assert.That(ExchangeRows(harness.Window), Has.Length.EqualTo(1));
    }

    [AvaloniaTest]
    public void EnterWithoutModifiers_InsertsNewlineInsteadOfSending()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "line one";
        harness.Window.PromptBox.Focus();
        harness.Window.PromptBox.CaretIndex = harness.Window.PromptBox.Text!.Length;
        harness.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.That(harness.Window.PromptBox.Text, Does.StartWith("line one"));
        Assert.That(harness.Window.PromptBox.Text, Does.Contain(Environment.NewLine).Or.Contain("\n"));
        Assert.That(ExchangeRows(harness.Window), Is.Empty);
    }

    [AvaloniaTest]
    public async Task SendAfterOpeningOldExchange_ContinuesThatDialog()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "old question";
        await harness.Window.SendAsync();
        var oldSession = harness.DialogManager.CurrentSession;

        await harness.Window.StartNewDialogAsync();
        Assert.That(harness.DialogManager.CurrentSession.Id, Is.Not.EqualTo(oldSession.Id));

        var exchange = harness.DialogManager.GetExchanges(oldSession.Id).Single();
        harness.Window.OpenExchange(oldSession, exchange);
        Assert.That(harness.DialogManager.CurrentSession.Id, Is.EqualTo(oldSession.Id));
        Assert.That(harness.Window.ContextValueText.Text, Is.EqualTo("1 exchange"));

        harness.Window.ReturnLiveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        harness.Window.PromptBox.Text = "follow-up";
        await harness.Window.SendAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.That(harness.DialogManager.CurrentSession.Id, Is.EqualTo(oldSession.Id));
        Assert.That(harness.DialogManager.GetExchanges(oldSession.Id).Select(p => p.Question),
            Is.EqualTo(new[] { "old question", "follow-up" }));
    }

    [AvaloniaTest]
    public async Task History_OneRowPerDialog_SecondSendUpdatesRowInsteadOfAddingNew()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "first question";
        await harness.Window.SendAsync();
        harness.Window.PromptBox.Text = "second question";
        await harness.Window.SendAsync();
        Dispatcher.UIThread.RunJobs();

        // Второй Send не плодит строку: диалог в истории один, со своим заголовком
        // (первый вопрос) и последним обменом.
        var session = harness.DialogManager.GetSessions().Single();
        var row = ExchangeRows(harness.Window).Single();
        var lastExchange = harness.DialogManager.GetExchanges(session.Id).Last();
        Assert.That((int)row.Tag!, Is.EqualTo(lastExchange.Id));
        var texts = (StackPanel)((Grid)row.Child!).Children[1];
        Assert.That(((TextBlock)texts.Children[0]).Text, Is.EqualTo("first question"));
    }

    [AvaloniaTest]
    public async Task History_DialogsOrderedByLastActivity_CurrentHighlighted()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "old dialog question";
        await harness.Window.SendAsync();
        var oldSession = harness.DialogManager.CurrentSession;

        await harness.Window.StartNewDialogAsync();
        harness.Window.PromptBox.Text = "new dialog question";
        await harness.Window.SendAsync();
        Dispatcher.UIThread.RunJobs();

        var rows = ExchangeRows(harness.Window);
        Assert.That(rows, Has.Length.EqualTo(2), "по строке на каждый диалог");

        // Возобновляем старый диалог и продолжаем его: его строка поднимается наверх
        // (последняя активность), новых строк не появляется.
        var oldExchange = harness.DialogManager.GetExchanges(oldSession.Id).Single();
        harness.Window.OpenExchange(oldSession, oldExchange);
        harness.Window.ReturnToLive();
        harness.Window.PromptBox.Text = "follow-up in old dialog";
        await harness.Window.SendAsync();
        Dispatcher.UIThread.RunJobs();

        rows = ExchangeRows(harness.Window);
        Assert.That(rows, Has.Length.EqualTo(2));
        var topExchangeId = (int)rows[0].Tag!;
        Assert.That(harness.DialogManager.GetExchanges(oldSession.Id).Select(p => p.Id), Does.Contain(topExchangeId));
    }

    [AvaloniaTest]
    public async Task HistoryRowClick_EntersViewMode()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "What is dense retrieval?";
        await harness.Window.SendAsync();
        harness.Window.PromptBox.Text = "next live draft";
        Dispatcher.UIThread.RunJobs();
        harness.Window.UpdateLayout();

        var row = ExchangeRows(harness.Window).Single();
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), harness.Window);
        Assert.That(point, Is.Not.Null);
        harness.Window.MouseDown(point!.Value, MouseButton.Left);
        harness.Window.MouseUp(point.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.That(harness.Window.ViewBanner.IsVisible, Is.True);
        Assert.That(harness.Window.ViewBannerText.Text, Does.StartWith("Viewing saved exchange · "));
        Assert.That(harness.Window.PromptBox.IsReadOnly, Is.True);
        Assert.That(harness.Window.PromptBox.Text, Is.EqualTo("What is dense retrieval?"));
        Assert.That(harness.Window.PromptHintText.Text, Is.EqualTo("read-only · saved"));
        Assert.That(harness.Window.SendButton.IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task ReturnToLive_RestoresLiveInput()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "What is dense retrieval?";
        await harness.Window.SendAsync();
        harness.Window.PromptBox.Text = "live draft question";

        var session = harness.DialogManager.GetSessions().Single();
        var exchange = harness.DialogManager.GetExchanges(session.Id).Single();
        harness.Window.OpenExchange(session, exchange);
        Assert.That(harness.Window.ViewBanner.IsVisible, Is.True);
        Assert.That(harness.Window.PromptBox.IsReadOnly, Is.True);

        harness.Window.ReturnLiveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.That(harness.Window.ViewBanner.IsVisible, Is.False);
        Assert.That(harness.Window.PromptBox.IsReadOnly, Is.False);
        Assert.That(harness.Window.PromptBox.Text, Is.EqualTo("live draft question"));
        Assert.That(harness.Window.PromptHintText.Text, Is.EqualTo("editable"));
        Assert.That(harness.Window.SendButton.IsEnabled, Is.True);
    }

    [AvaloniaTest]
    public async Task Reask_CopiesQuestionIntoLiveInput()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "What is dense retrieval?";
        await harness.Window.SendAsync();

        var session = harness.DialogManager.GetSessions().Single();
        var exchange = harness.DialogManager.GetExchanges(session.Id).Single();
        harness.Window.OpenExchange(session, exchange);

        harness.Window.ReaskButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.That(harness.Window.ViewBanner.IsVisible, Is.False);
        Assert.That(harness.Window.PromptBox.IsReadOnly, Is.False);
        Assert.That(harness.Window.PromptBox.Text, Is.EqualTo("What is dense retrieval?"));
        Assert.That(harness.Window.SendButton.IsEnabled, Is.True);
    }

    [AvaloniaTest]
    public async Task NewDialog_ClearsWorkbench()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "What is dense retrieval?";
        await harness.Window.SendAsync();
        Assert.That(harness.Window.ContextValueText.Text, Is.EqualTo("1 exchange"));

        await harness.Window.StartNewDialogAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.That(harness.Window.PromptBox.Text, Is.Null.Or.Empty);
        Assert.That(harness.Window.AnswerPlaceholder.IsVisible, Is.True);
        Assert.That(harness.Window.AnswerBlock.IsVisible, Is.False);
        Assert.That(harness.Window.SourcesCountText.Text, Is.EqualTo("none yet"));
        Assert.That(harness.Window.SourcesEmptyPanel.IsVisible, Is.True);
        Assert.That(harness.Window.ContextValueText.Text, Is.EqualTo("0 exchanges"));
        // Старый обмен остался в истории, плюс строка новой пустой сессии.
        Assert.That(ExchangeRows(harness.Window), Has.Length.EqualTo(1));
        Assert.That(harness.Window.HistoryPanel.Children.OfType<Border>().Count(b => b.Tag == null), Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ContextIndicator_TracksExchangesOfCurrentSession()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        Assert.That(harness.Window.ContextValueText.Text, Is.EqualTo("0 exchanges"));
        Assert.That(harness.Window.MaxDepthText.Text, Is.EqualTo("max 20"));

        harness.Window.PromptBox.Text = "What is dense retrieval?";
        await harness.Window.SendAsync();
        Assert.That(harness.Window.ContextValueText.Text, Is.EqualTo("1 exchange"));

        harness.Window.PromptBox.Text = "And what about sparse retrieval?";
        await harness.Window.SendAsync();
        Assert.That(harness.Window.ContextValueText.Text, Is.EqualTo("2 exchanges"));

        // Контекст диалога ушёл в промпт второй генерации.
        Assert.That(harness.Llm.LastPrompt, Does.Contain("Q: What is dense retrieval?"));
    }

    [AvaloniaTest]
    public async Task DeleteSession_RemovesDialogRowAndUpdatesContext()
    {
        var harness = CreateWindow();
        harness.Window.Show();

        harness.Window.PromptBox.Text = "What is dense retrieval?";
        await harness.Window.SendAsync();
        var sessionId = harness.DialogManager.CurrentSession.Id;
        Assert.That(ExchangeRows(harness.Window), Has.Length.EqualTo(1));

        await harness.Window.DeleteSessionAsync(sessionId);
        Dispatcher.UIThread.RunJobs();

        Assert.That(ExchangeRows(harness.Window), Is.Empty);
        Assert.That(harness.Window.ContextValueText.Text, Is.EqualTo("0 exchanges"));
    }

    /// <summary>
    /// LLM с управляемым завершением: позволяет «подвесить» генерацию и докрутить её из теста.
    /// Для следующей подвешенной генерации — <see cref="ResetPending"/>.
    /// </summary>
    private sealed class ManualLlm : AbstractLlmInferences
    {
        public TaskCompletionSource<string> Pending { get; private set; } = new();

        public bool UsePending { get; set; }

        public void ResetPending() => Pending = new TaskCompletionSource<string>();

        public override Task<string> Question(string input)
            => UsePending ? Pending.Task : Task.FromResult("instant answer");
    }

    private static Harness CreateManualWindow()
    {
        var builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseInMemoryDatabase("RagWindowTests_" + Guid.NewGuid());

        SourceLensContext GetContext()
        {
            var ctx = new SourceLensContext(builder.Options);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        var llm = new ManualLlm();
        var dialogManager = new RagDialogManager(GetContext, () => llm, new StubRetriever(),
            new RetrievalOptions { TopK = 5, MinQueryLength = 1 }, new RagDialogOptions());
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(GetContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));
        var window = new RagWindow(
            engineManager,
            new TranscriptFactory(new StubTranscriptor()),
            new UiRecorder(new StubRecorder()),
            dialogManager,
            null,
            (_, _) => Task.FromResult(new CliProbeResult(true, "1.0.0", "/usr/bin/stub")),
            CreateEngineOptions());
        return new Harness { Window = window, Llm = null!, DialogManager = dialogManager, ManualLlm = llm };
    }

    [AvaloniaTest]
    public async Task Send_PreviousAnswerStaysVisibleWhileGenerating_OnEverySend()
    {
        var harness = CreateManualWindow();
        var window = harness.Window;
        var llm = harness.ManualLlm!;
        window.Show();

        // Первая генерация: ответа ещё нет — остаётся плейсхолдер.
        llm.UsePending = true;
        window.PromptBox.Text = "question one";
        var send = window.SendAsync();
        Assert.That(window.AnswerPlaceholder.IsVisible, Is.True, "до первого ответа — плейсхолдер");
        llm.Pending.SetResult("answer one");
        await send;
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.AnswerBlock.Inlines!.Text, Is.EqualTo("answer one"));

        // Вторая генерация: первый ответ не затирается, по готовности заменяется вторым.
        llm.ResetPending();
        window.PromptBox.Text = "question two";
        send = window.SendAsync();
        Assert.That(window.AnswerBlock.Inlines!.Text, Is.EqualTo("answer one"),
            "первый ответ виден, пока генерируется второй");
        llm.Pending.SetResult("answer two");
        await send;
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.AnswerBlock.Inlines!.Text, Is.EqualTo("answer two"),
            "по готовности второй ответ заменяет первый");

        // Третья генерация: второй ответ не затирается, по готовности заменяется третьим.
        llm.ResetPending();
        window.PromptBox.Text = "question three";
        send = window.SendAsync();
        Assert.That(window.AnswerBlock.Inlines!.Text, Is.EqualTo("answer two"),
            "второй ответ виден, пока генерируется третий");
        llm.Pending.SetResult("answer three");
        await send;
        Dispatcher.UIThread.RunJobs();
        Assert.That(window.AnswerBlock.Inlines!.Text, Is.EqualTo("answer three"),
            "по готовности третий ответ заменяет второй");
    }

    /// <summary>
    /// Пиксели области ответа из реального кадра окна (Skia): ловит баги перерисовки,
    /// которые не видны на уровне Inlines (Avalonia 11.3 не перерисовывает SelectableTextBlock
    /// при замене контента с теми же arrange-bounds).
    /// </summary>
    private static byte[] CaptureAnswerRegion(RagWindow window)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame()!;
        var origin = window.AnswerScroll.TranslatePoint(new Point(0, 0), window)!.Value;
        using var fb = frame.Lock();
        var x = (int)origin.X;
        var y = (int)origin.Y;
        var width = (int)window.AnswerScroll.Bounds.Width;
        var height = (int)window.AnswerScroll.Bounds.Height;
        var bytes = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Marshal.Copy(fb.Address + (y + row) * fb.RowBytes + x * 4, bytes, row * width * 4, width * 4);
        return bytes;
    }

    [AvaloniaTest]
    public async Task Send_AnswerPaneActuallyRepaints_OnEveryNewAnswer()
    {
        var harness = CreateManualWindow();
        var window = harness.Window;
        var llm = harness.ManualLlm!;
        window.Show();

        llm.UsePending = true;
        window.PromptBox.Text = "question one";
        var send = window.SendAsync();
        llm.Pending.SetResult("first answer");
        await send;
        Dispatcher.UIThread.RunJobs();
        var afterFirst = CaptureAnswerRegion(window);

        llm.ResetPending();
        window.PromptBox.Text = "question two";
        send = window.SendAsync();
        Assert.That(CaptureAnswerRegion(window).SequenceEqual(afterFirst), Is.True,
            "пока генерируется второй ответ, на экране остаётся отрисованный первый");
        llm.Pending.SetResult("other answer");
        await send;
        Dispatcher.UIThread.RunJobs();
        var afterSecond = CaptureAnswerRegion(window);
        Assert.That(afterSecond.SequenceEqual(afterFirst), Is.False,
            "готовый второй ответ обязан реально перерисоваться поверх первого");
    }

    [AvaloniaTest]
    public async Task AnswerReady_WhileViewingSavedExchange_ReturnsToActiveDialogWithAnswer()
    {
        var builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseInMemoryDatabase("RagWindowTests_" + Guid.NewGuid());

        SourceLensContext GetContext()
        {
            var ctx = new SourceLensContext(builder.Options);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        var llm = new ManualLlm();
        var dialogManager = new RagDialogManager(GetContext, () => llm, new StubRetriever(),
            new RetrievalOptions { TopK = 5, MinQueryLength = 1 }, new RagDialogOptions());
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(GetContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));
        var window = new RagWindow(
            engineManager,
            new TranscriptFactory(new StubTranscriptor()),
            new UiRecorder(new StubRecorder()),
            dialogManager,
            null,
            (_, _) => Task.FromResult(new CliProbeResult(true, "1.0.0", "/usr/bin/stub")),
            CreateEngineOptions());
        window.Show();

        window.PromptBox.Text = "first question";
        await window.SendAsync();
        var session = dialogManager.CurrentSession;
        var firstExchange = dialogManager.GetExchanges(session.Id).Single();

        // Пока генерируется второй ответ, пользователь открывает сохранённый обмен.
        llm.UsePending = true;
        window.PromptBox.Text = "second question";
        var sendTask = window.SendAsync();
        window.OpenExchange(session, firstExchange);
        Assert.That(window.ViewBanner.IsVisible, Is.True);

        llm.Pending.SetResult("the fresh answer");
        Dispatcher.UIThread.RunJobs();
        await sendTask;
        Dispatcher.UIThread.RunJobs();

        // По готовности ответа просмотр закрывается — активный диалог с заполненным Answer на экране.
        Assert.That(window.ViewBanner.IsVisible, Is.False);
        Assert.That(window.AnswerBlock.Inlines!.Text, Is.EqualTo("the fresh answer"));
        Assert.That(window.PromptBox.Text, Is.EqualTo("second question"));
        Assert.That(window.PromptBox.IsReadOnly, Is.False);
        Assert.That(window.ContextValueText.Text, Is.EqualTo("2 exchanges"));
    }

    [AvaloniaTest]
    public void Startup_NewDialogIsCurrent_OldDialogStaysInHistory()
    {
        var builder = new DbContextOptionsBuilder<SourceLensContext>()
            .UseInMemoryDatabase("RagWindowTests_" + Guid.NewGuid());

        SourceLensContext GetContext()
        {
            var ctx = new SourceLensContext(builder.Options);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        // Сессия с обменом существует до создания окна — старт должен открыть НОВЫЙ диалог,
        // а старый оставить строкой в History.
        using (var ctx = GetContext())
        {
            var session = ctx.AddRagSession("Old question").GetAwaiter().GetResult();
            ctx.SaveChanges();
            ctx.AddRagExchange(session, "Old question", "[]").GetAwaiter().GetResult().SetAnswer("Old answer");
            ctx.SaveChanges();
        }

        var llm = new CapturingLlm();
        var dialogManager = new RagDialogManager(GetContext, () => llm, new StubRetriever(),
            new RetrievalOptions { TopK = 5, MinQueryLength = 1 }, new RagDialogOptions());
        var engineManager = new AnswerEngineManager((_, _) => llm,
            new EngineSettings(GetContext, EngineSettings.ClaudeProvider, "sonnet", "gpt-5-codex"));
        var window = new RagWindow(
            engineManager,
            new TranscriptFactory(new StubTranscriptor()),
            new UiRecorder(new StubRecorder()),
            dialogManager,
            null,
            (_, _) => Task.FromResult(new CliProbeResult(true, "1.0.0", "/usr/bin/stub")),
            CreateEngineOptions());
        window.Show();

        Assert.That(ExchangeRows(window), Has.Length.EqualTo(1), "старый диалог — строкой в истории");
        Assert.That(window.HistoryPanel.Children.OfType<Border>().Count(b => b.Tag == null), Is.EqualTo(1),
            "плюс строка нового пустого диалога");
        Assert.That(window.ContextValueText.Text, Is.EqualTo("0 exchanges"), "старт — новый диалог, контекст пуст");
        Assert.That(window.EngineLabelText.Text, Is.EqualTo("Claude · sonnet"));
    }
}
