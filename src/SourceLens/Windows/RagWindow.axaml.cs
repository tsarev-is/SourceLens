using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SourceLens.Configuration;
using SourceLens.Domain;
using SourceLens.Domain.Audio;
using SourceLens.Domain.Entities.Models;
using SourceLens.Domain.Rag.Models;
using SourceLens.Integrations;
using SourceLens.Integrations.Cli;

namespace SourceLens.Windows;

/// <summary>
/// Главное окно RAG-режима (строго по мокапу docs/RagWindow.dc.html, code-behind без MVVM).
/// </summary>
public partial class RagWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // Палитра мокапа.
    private static readonly IBrush StatusReadyBrush = Brush.Parse("#4ec98a");
    private static readonly IBrush StatusBusyBrush = Brush.Parse("#e3b341");
    private static readonly IBrush StatusErrorBrush = Brush.Parse("#e5534b");
    private static readonly IBrush AccentBrush = Brush.Parse("#6aa6ff");
    private static readonly IBrush PendingBrush = Brush.Parse("#5b626e");
    private static readonly IBrush RecordIdleDotBrush = Brush.Parse("#7a818d");
    private static readonly IBrush RecordActiveBgBrush = Brush.Parse("#1FE5534B");
    private static readonly IBrush RecordActiveBorderBrush = Brush.Parse("#73E5534B");
    private static readonly IBrush RecordActiveFgBrush = Brush.Parse("#e08a85");
    private static readonly IBrush SelectedRowBrush = Brush.Parse("#1F6AA6FF");
    private static readonly IBrush RowDotBrush = Brush.Parse("#3a4150");
    private static readonly IBrush EmptyRowDotBrush = Brush.Parse("#2c313b");
    private static readonly IBrush RowQuestionBrush = Brush.Parse("#c3c8d1");
    private static readonly IBrush RowQuestionSelectedBrush = Brush.Parse("#dfe3ea");
    private static readonly IBrush RowMutedBrush = Brush.Parse("#5b626e");
    private static readonly FontFamily MonoFont = new("Consolas,Menlo,monospace");

    private enum UiState
    {
        Ready,
        Busy,
        Error,
    }

    private readonly AnswerEngineManager _engineManager;
    private readonly TranscriptFactory _transcriptFactory;
    private readonly UiRecorder _inputRecorder;
    private readonly RagDialogManager _dialogManager;
    private readonly SourceLibraryManager? _libraryManager;
    private readonly Func<EngineOption, Task<CliProbeResult>> _probe;
    private readonly EngineOption[] _engineOptions;

    private UiState _state = UiState.Ready;
    private RagExchangeView? _viewExchange;
    private string _livePrompt = string.Empty;
    private string _liveAnswer = string.Empty;
    private KnowledgeChunk[] _liveSources = Array.Empty<KnowledgeChunk>();
    private CancellationTokenSource? _summaryCts;
    private readonly Dictionary<string, string> _summaryCache = new();

    public RagWindow(
        AnswerEngineManager engineManager,
        TranscriptFactory transcriptFactory,
        UiRecorder inputRecorder,
        RagDialogManager dialogManager,
        SourceLibraryManager? libraryManager,
        Func<EngineOption, Task<CliProbeResult>> probe,
        EngineOption[] engineOptions)
    {
        _engineManager = engineManager;
        _transcriptFactory = transcriptFactory;
        _inputRecorder = inputRecorder;
        _dialogManager = dialogManager;
        _libraryManager = libraryManager;
        _probe = probe;
        _engineOptions = engineOptions;

        InitializeComponent();

        // Tunnel: при AcceptsReturn TextBox сам обрабатывает Enter (вставляет перевод строки и помечает
        // событие Handled) до bubbling-подписчиков, поэтому Ctrl/Cmd+Enter перехватываем на туннельной фазе.
        PromptBox.AddHandler(KeyDownEvent, PromptBox_OnKeyDown, RoutingStrategies.Tunnel);

        // RAG выключен — управлять источниками нечем, кнопку прячем.
        SourcesButton.IsVisible = _libraryManager != null;

        // TextChanged может приходить отложенно; PropertyChanged по TextProperty — синхронно.
        PromptBox.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty)
                return;

            if (_viewExchange == null)
                _livePrompt = PromptBox.Text ?? string.Empty;
            UpdateSendState();
        };

        EngineLabelText.Text = _engineManager.EngineLabel;
        MaxDepthText.Text = $"max {_dialogManager.Options.HistoryDepth}";

        // Старт: последняя сессия уже поднята менеджером — «восстановленный контекст».
        ReloadHistory();
        UpdateContextIndicator();
        RenderAnswer(string.Empty);
        RenderSources(Array.Empty<KnowledgeChunk>());
        SetReady();
        UpdateSendState();
    }

    // ---------- Статус-бар ----------

    private void SetReady()
    {
        _state = UiState.Ready;
        StatusText.Text = "Ready";
        StatusDot.Fill = StatusReadyBrush;
        AnswerStatusText.Text = string.Empty;
        UpdateSendState();
    }

    private void SetBusy(RagPhase phase)
    {
        _state = UiState.Busy;
        StatusText.Text = phase == RagPhase.Retrieving ? "Retrieving sources…" : "Generating answer…";
        StatusDot.Fill = StatusBusyBrush;
        AnswerStatusText.Text = phase == RagPhase.Retrieving ? "retrieving…" : "generating…";
        UpdateSendState();
    }

    private void SetBusy(string statusText)
    {
        _state = UiState.Busy;
        StatusText.Text = statusText;
        StatusDot.Fill = StatusBusyBrush;
        AnswerStatusText.Text = string.Empty;
        UpdateSendState();
    }

    private void SetError()
    {
        _state = UiState.Error;
        StatusText.Text = "Error";
        StatusDot.Fill = StatusErrorBrush;
        AnswerStatusText.Text = string.Empty;
        UpdateSendState();
    }

    private void SetRecordingStatus()
    {
        StatusText.Text = "Recording voice…";
        StatusDot.Fill = StatusErrorBrush;
        AnswerStatusText.Text = string.Empty;
    }

    private async void SourcesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_libraryManager == null)
                return;

            await new SourceLibraryWindow(_libraryManager).ShowDialog(this);
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "Source library window failed");
        }
    }

    private async void SettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new SettingsWindow(_engineManager, _engineOptions, _probe,
                () => EngineLabelText.Text = _engineManager.EngineLabel);
            await settings.ShowDialog(this);
            EngineLabelText.Text = _engineManager.EngineLabel;
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "Settings window failed");
        }
    }

    // ---------- Левая панель истории ----------

    internal void ReloadHistory()
    {
        HistoryPanel.Children.Clear();

        // Плоский список по дате (новые сверху): обмен возобновлённого старого диалога
        // должен встать под сегодняшний разделитель, а не под дату создания сессии.
        var rows = new List<(DateTimeOffset At, int Order, RagSessionItem Session, RagExchangeView? Exchange)>();
        foreach (var session in _dialogManager.GetSessions())
        {
            var exchanges = _dialogManager.GetExchanges(session.Id);
            if (exchanges.Length == 0)
                rows.Add((session.CreatedAt, 0, session, null));
            foreach (var exchange in exchanges)
                rows.Add((exchange.CreatedAt, exchange.Id, session, exchange));
        }

        string? lastDate = null;
        foreach (var row in rows.OrderByDescending(p => p.At).ThenByDescending(p => p.Order))
        {
            var label = FormatDateLabel(row.At.LocalDateTime);
            if (label != lastDate)
            {
                HistoryPanel.Children.Add(BuildDateDivider(label));
                lastDate = label;
            }

            HistoryPanel.Children.Add(BuildHistoryRow(row.Session, row.Exchange));
        }

        HistoryEmptyText.IsVisible = rows.Count == 0;
    }

    private static string FormatDateLabel(DateTime date)
    {
        var label = date.ToString("dd MMM", CultureInfo.InvariantCulture).ToUpperInvariant();
        return date.Date == DateTime.Today ? $"TODAY · {label}" : label;
    }

    private static Control BuildDateDivider(string label)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.7,
            Foreground = RowMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var line = new Rectangle
        {
            Height = 1,
            Fill = Brush.Parse("#1f2229"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(line, 1);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(8, 13, 8, 7),
        };
        grid.Children.Add(text);
        grid.Children.Add(line);
        return grid;
    }

    private Border BuildHistoryRow(RagSessionItem session, RagExchangeView? exchange)
    {
        var isEmpty = exchange == null;
        var selected = exchange != null && _viewExchange?.Id == exchange.Id;

        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Margin = new Thickness(0, 5, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = selected ? AccentBrush : isEmpty ? EmptyRowDotBrush : RowDotBrush,
        };

        var question = new TextBlock
        {
            Text = isEmpty ? "(empty dialog)" : Truncate(exchange!.Question, 50),
            FontSize = 12,
            Foreground = isEmpty ? RowMutedBrush : selected ? RowQuestionSelectedBrush : RowQuestionBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var snippet = new TextBlock
        {
            Text = isEmpty
                ? session.Title ?? "New dialog"
                : Truncate(CollapseWhitespace(exchange!.Answer), 62),
            FontSize = 10.5,
            Foreground = RowMutedBrush,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var texts = new StackPanel();
        texts.Children.Add(question);
        texts.Children.Add(snippet);
        Grid.SetColumn(texts, 1);

        var time = new TextBlock
        {
            Text = isEmpty ? string.Empty : exchange!.CreatedAt.LocalDateTime.ToString("HH:mm"),
            FontSize = 10.5,
            Foreground = RowMutedBrush,
            FontFamily = MonoFont,
            Margin = new Thickness(8, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(time, 2);

        var delete = new Button
        {
            Content = "✕",
            FontSize = 12,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            Foreground = Brush.Parse("#3d4350"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        delete.Classes.Add("histDelete");
        ToolTip.SetTip(delete, "Delete from history");
        delete.Click += async (_, _) =>
        {
            try
            {
                if (isEmpty)
                    await DeleteSessionAsync(session.Id);
                else
                    await DeleteExchangeAsync(exchange!.Id);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "History delete failed");
            }
        };
        Grid.SetColumn(delete, 3);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(texts);
        grid.Children.Add(time);
        grid.Children.Add(delete);

        var row = new Border
        {
            Background = selected ? SelectedRowBrush : Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7),
            Margin = new Thickness(0, 0, 0, 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
            Tag = exchange?.Id,
        };
        row.Classes.Add("histRow");
        if (exchange != null)
            row.PointerPressed += (_, _) => OpenExchange(session, exchange);
        else
            row.PointerPressed += (_, _) => { };

        return row;
    }

    internal async Task DeleteExchangeAsync(int exchangeId)
    {
        await _dialogManager.DeleteExchange(exchangeId);
        if (_viewExchange?.Id == exchangeId)
            ReturnToLive();
        else
            ReloadHistory();
        UpdateContextIndicator();
    }

    internal async Task DeleteSessionAsync(int sessionId)
    {
        await _dialogManager.DeleteSession(sessionId);
        ReloadHistory();
        UpdateContextIndicator();
    }

    internal void UpdateContextIndicator()
    {
        var pairs = _dialogManager.ContextSize;
        ContextValueText.Text = $"{pairs} exchange{(pairs == 1 ? string.Empty : "s")}";
    }

    private async void NewDialogButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await StartNewDialogAsync();
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "New dialog failed");
            SetError();
        }
    }

    internal async Task StartNewDialogAsync()
    {
        await _dialogManager.StartNewSession();
        _viewExchange = null;
        ViewBanner.IsVisible = false;
        PromptBox.IsReadOnly = false;
        PromptHintText.Text = "editable";
        _livePrompt = string.Empty;
        _liveAnswer = string.Empty;
        _liveSources = Array.Empty<KnowledgeChunk>();
        PromptBox.Text = string.Empty;
        RenderAnswer(string.Empty);
        RenderSources(Array.Empty<KnowledgeChunk>());
        ReloadHistory();
        UpdateContextIndicator();
        SetReady();
    }

    // ---------- Режим просмотра сохранённого обмена ----------

    internal void OpenExchange(RagSessionItem session, RagExchangeView exchange)
    {
        // Открытый из истории диалог становится текущим: следующий Send продолжит его,
        // а не последнюю/новую сессию.
        _dialogManager.ResumeSession(session.Id);
        UpdateContextIndicator();

        _viewExchange = exchange;
        ViewBannerText.Text = $"Viewing saved exchange · {(string.IsNullOrWhiteSpace(session.Title) ? "dialog" : session.Title)}";
        ViewBanner.IsVisible = true;
        PromptBox.Text = exchange.Question;
        PromptBox.IsReadOnly = true;
        PromptHintText.Text = "read-only · saved";
        RenderAnswer(exchange.Answer);
        RenderSources(exchange.Sources);
        ReloadHistory();
        UpdateSendState();
    }

    internal void ReturnToLive()
    {
        _viewExchange = null;
        ViewBanner.IsVisible = false;
        PromptBox.IsReadOnly = false;
        PromptHintText.Text = "editable";
        PromptBox.Text = _livePrompt;
        if (_state == UiState.Busy)
            ShowAnswerPending();
        else
            RenderAnswer(_liveAnswer);
        RenderSources(_liveSources);
        ReloadHistory();
        UpdateSendState();
    }

    private void ReturnLiveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ReturnToLive();
    }

    private void ReaskButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewExchange == null)
            return;

        var question = _viewExchange.Question;
        ReturnToLive();
        PromptBox.Text = question;
        UpdateSendState();
    }

    // ---------- Промпт и Send ----------

    private void PromptBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            e.Handled = true;
            _ = SendAsync();
            return;
        }

        // Начало нового ввода снимает режим просмотра.
        if (_viewExchange != null && IsTypingKey(e))
            ReturnToLive();
    }

    private static bool IsTypingKey(KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) != 0)
            return false;

        return e.Key
            is >= Key.A and <= Key.Z
            or >= Key.D0 and <= Key.D9
            or >= Key.NumPad0 and <= Key.NumPad9
            or Key.Space or Key.Back or Key.Delete or Key.Enter
            or Key.OemPeriod or Key.OemComma or Key.OemQuestion or Key.OemMinus or Key.OemPlus
            or Key.OemQuotes or Key.OemSemicolon or Key.OemTilde;
    }

    private void UpdateSendState()
    {
        SendButton.IsEnabled = _state != UiState.Busy
                               && !_inputRecorder.IsRecording
                               && _viewExchange == null
                               && !string.IsNullOrWhiteSpace(PromptBox.Text);
    }

    private async void SendButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await SendAsync();
    }

    internal async Task SendAsync()
    {
        if (_state == UiState.Busy || _inputRecorder.IsRecording || _viewExchange != null)
            return;

        var question = (PromptBox.Text ?? string.Empty).Trim();
        if (question.Length == 0)
            return;

        _summaryCts?.Cancel();
        _summaryCts = new CancellationTokenSource();

        SetBusy(RagPhase.Retrieving);
        ShowAnswerPending();
        _liveAnswer = string.Empty;
        _liveSources = Array.Empty<KnowledgeChunk>();
        RenderSources(Array.Empty<KnowledgeChunk>());

        try
        {
            var progress = new UiPhaseProgress(phase =>
            {
                if (_state == UiState.Busy)
                    SetBusy(phase);
            });
            var result = await _dialogManager.Ask(question, _summaryCts.Token, progress);

            _liveAnswer = result.Answer;
            _liveSources = result.Sources;
            _livePrompt = string.Empty;
            if (_viewExchange == null)
            {
                RenderAnswer(result.Answer);
                RenderSources(result.Sources);
                PromptBox.Text = string.Empty;
            }

            ReloadHistory();
            UpdateContextIndicator();
            SetReady();
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "RAG ask failed");
            SetError();
            if (_viewExchange == null)
                RenderAnswer($"Error: {exception.Message}");
        }
        finally
        {
            UpdateSendState();
        }
    }

    /// <summary>
    /// Фаза приходит с UI-потока (продолжения Ask без ConfigureAwait) — применяем синхронно.
    /// </summary>
    private sealed class UiPhaseProgress : IProgress<RagPhase>
    {
        private readonly Action<RagPhase> _apply;

        public UiPhaseProgress(Action<RagPhase> apply)
        {
            _apply = apply;
        }

        public void Report(RagPhase value)
        {
            if (Dispatcher.UIThread.CheckAccess())
                _apply(value);
            else
                Dispatcher.UIThread.Post(() => _apply(value));
        }
    }

    // ---------- Запись голоса ----------

    private async void RecordButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!_inputRecorder.IsRecording)
            {
                if (_viewExchange != null)
                    ReturnToLive();

                _inputRecorder.RunRecording();
                ApplyRecordingUi(true);
                SetRecordingStatus();
                UpdateSendState();
                return;
            }

            var pcm = await _inputRecorder.GetRecord();
            ApplyRecordingUi(false);
            SetBusy("Transcribing…");

            var audioOptions = _inputRecorder.GetAudioOptions();
            var transcript = await Task.Run(async () =>
            {
                var wav = pcm.ToWav(audioOptions);
                var transcriptor = _transcriptFactory.Acquire();
                try
                {
                    return await transcriptor.Transcript(wav);
                }
                finally
                {
                    _transcriptFactory.Release(transcriptor);
                }
            });

            PromptBox.Text = transcript;
            SetReady();
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "Voice request failed");
            ApplyRecordingUi(false);
            SetError();
        }
        finally
        {
            UpdateSendState();
        }
    }

    private void ApplyRecordingUi(bool recording)
    {
        RecordLabel.Text = recording ? "Stop" : "Record voice";
        RecordDot.Fill = recording ? StatusErrorBrush : RecordIdleDotBrush;
        RecordDot.Classes.Set("pulse", recording);
        ListeningPanel.IsVisible = recording;
        if (recording)
        {
            RecordButton.Background = RecordActiveBgBrush;
            RecordButton.BorderBrush = RecordActiveBorderBrush;
            RecordButton.Foreground = RecordActiveFgBrush;
        }
        else
        {
            RecordButton.ClearValue(BackgroundProperty);
            RecordButton.ClearValue(BorderBrushProperty);
            RecordButton.ClearValue(ForegroundProperty);
        }
    }

    // ---------- ANSWER ----------

    private void RenderAnswer(string text)
    {
        AnswerBlock.Inlines!.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            AnswerBlock.IsVisible = false;
            AnswerPlaceholder.IsVisible = true;
            return;
        }

        AnswerPlaceholder.IsVisible = false;
        AnswerBlock.IsVisible = true;
        foreach (var part in Regex.Split(text, @"(\[\d+\])"))
        {
            if (part.Length == 0)
                continue;

            if (Regex.IsMatch(part, @"^\[\d+\]$"))
            {
                AnswerBlock.Inlines.Add(new Run(part)
                {
                    Foreground = AccentBrush,
                    FontWeight = FontWeight.SemiBold,
                    FontFamily = MonoFont,
                    FontSize = 12,
                });
            }
            else
            {
                AnswerBlock.Inlines.Add(new Run(part));
            }
        }
    }

    private void ShowAnswerPending()
    {
        AnswerPlaceholder.IsVisible = false;
        AnswerBlock.IsVisible = true;
        AnswerBlock.Inlines!.Clear();
        AnswerBlock.Inlines.Add(new Run("…") { Foreground = PendingBrush });
    }

    // ---------- SOURCES ----------

    private void RenderSources(KnowledgeChunk[] chunks)
    {
        SourcesPanel.Children.Clear();
        var any = chunks.Length > 0;
        SourcesEmptyPanel.IsVisible = !any;
        SourcesScroll.IsVisible = any;
        SourcesCountText.Text = any ? $"top-{chunks.Length}" : "none yet";
        for (var i = 0; i < chunks.Length; i++)
            SourcesPanel.Children.Add(BuildSourceCard(i + 1, chunks[i]));
    }

    private Border BuildSourceCard(int index, KnowledgeChunk chunk)
    {
        var number = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(5),
            Background = Brush.Parse("#101216"),
            BorderBrush = Brush.Parse("#2f3441"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = index.ToString(),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = AccentBrush,
                FontFamily = MonoFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var title = new TextBlock
        {
            Text = chunk.SourceTitle,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#dfe3ea"),
            TextWrapping = TextWrapping.Wrap,
        };
        var location = new TextBlock
        {
            Text = chunk.SourceLocation,
            FontSize = 11,
            Foreground = Brush.Parse("#6b7280"),
            FontFamily = MonoFont,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(location);
        Grid.SetColumn(titles, 1);

        var scoreText = new TextBlock
        {
            Text = chunk.Score.ToString("0.00", CultureInfo.InvariantCulture),
            FontSize = 11,
            Foreground = Brush.Parse("#9aa1ad"),
            FontFamily = MonoFont,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var barColor = chunk.Score >= 0.8f ? "#4ec98a" : chunk.Score >= 0.72f ? "#6aa6ff" : "#e3b341";
        var bar = new Border
        {
            Width = 54,
            Height = 4,
            CornerRadius = new CornerRadius(3),
            Background = Brush.Parse("#262a33"),
            Margin = new Thickness(0, 4, 0, 0),
            Child = new Border
            {
                Width = Math.Clamp(Math.Round(chunk.Score * 54), 0, 54),
                Height = 4,
                CornerRadius = new CornerRadius(3),
                Background = Brush.Parse(barColor),
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };
        var scorePanel = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        scorePanel.Children.Add(scoreText);
        scorePanel.Children.Add(bar);
        Grid.SetColumn(scorePanel, 2);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(number);
        header.Children.Add(titles);
        header.Children.Add(scorePanel);

        var summarizeBtn = new Button
        {
            Content = "Summarize",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 4),
        };
        summarizeBtn.Classes.Add("chip");
        var fullBtn = new Button
        {
            Content = "Show full",
            FontSize = 11,
            Foreground = Brush.Parse("#9aa1ad"),
            Padding = new Thickness(10, 4),
        };
        fullBtn.Classes.Add("chip");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(summarizeBtn);
        buttons.Children.Add(fullBtn);

        var summaryText = new TextBlock
        {
            FontSize = 12,
            LineHeight = 18,
            Foreground = Brush.Parse("#b8c6e0"),
            TextWrapping = TextWrapping.Wrap,
        };
        var summaryStack = new StackPanel();
        summaryStack.Children.Add(new TextBlock
        {
            Text = "SUMMARY",
            FontSize = 9.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.6,
            Foreground = AccentBrush,
            Margin = new Thickness(0, 0, 0, 4),
        });
        summaryStack.Children.Add(summaryText);
        var summaryBlock = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 9, 0, 0),
            Padding = new Thickness(11, 9),
            Background = Brush.Parse("#0F6AA6FF"),
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Child = summaryStack,
        };

        var fullBlock = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 9, 0, 0),
            Padding = new Thickness(12, 10),
            Background = Brush.Parse("#0d0f13"),
            BorderBrush = Brush.Parse("#23262e"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = chunk.Text,
                FontSize = 12,
                LineHeight = 19,
                Foreground = Brush.Parse("#aab1bd"),
                TextWrapping = TextWrapping.Wrap,
            },
        };

        summarizeBtn.Click += async (_, _) =>
        {
            if (summaryBlock.IsVisible)
            {
                summaryBlock.IsVisible = false;
                summarizeBtn.Content = "Summarize";
                return;
            }

            var ct = _summaryCts?.Token ?? CancellationToken.None;
            if (!_summaryCache.TryGetValue(chunk.Text, out var summary))
            {
                summarizeBtn.Content = "Summarizing…";
                summarizeBtn.IsEnabled = false;
                try
                {
                    summary = await _engineManager.Current.SummariseChunk(chunk.Text);
                    summary = string.IsNullOrWhiteSpace(summary) ? "[no summary]" : summary.Trim();
                    _summaryCache[chunk.Text] = summary;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Chunk summary failed");
                    summary = "[summary unavailable]";
                }
                finally
                {
                    summarizeBtn.IsEnabled = true;
                }

                if (ct.IsCancellationRequested)
                {
                    summarizeBtn.Content = "Summarize";
                    return;
                }
            }

            summaryText.Text = summary;
            summaryBlock.IsVisible = true;
            summarizeBtn.Content = "Hide summary";
        };

        fullBtn.Click += (_, _) =>
        {
            fullBlock.IsVisible = !fullBlock.IsVisible;
            fullBtn.Content = fullBlock.IsVisible ? "Hide full" : "Show full";
        };

        var inner = new StackPanel();
        inner.Children.Add(header);
        inner.Children.Add(buttons);
        inner.Children.Add(summaryBlock);
        inner.Children.Add(fullBlock);

        return new Border
        {
            Background = Brush.Parse("#1a1d23"),
            BorderBrush = Brush.Parse("#262a33"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 11),
            Child = inner,
        };
    }

    // ---------- Утилиты ----------

    private static string Truncate(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private static string CollapseWhitespace(string text)
    {
        return Regex.Replace(text.Replace("•", " "), @"\s+", " ").Trim();
    }
}
