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
using SourceLens.Domain.Entities;
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
    private readonly Func<EngineOption, string, Task<CliProbeResult>> _probe;
    private readonly EngineOption[] _engineOptions;

    private UiState _state = UiState.Ready;
    private RagExchangeView? _viewExchange;
    private int? _viewSessionId;
    private string _livePrompt = string.Empty;
    private string _liveAnswer = string.Empty;
    private KnowledgeChunk[] _liveSources = Array.Empty<KnowledgeChunk>();
    private CancellationTokenSource? _summaryCts;
    private readonly Dictionary<string, string> _summaryCache = new();

    // Состояние scope-меню: текст фильтра и развёрнутые коллекции (для вложенного выбора документов).
    private string _scopeFilter = string.Empty;
    private readonly HashSet<int> _expandedScopeCollections = new();
    // Бейдж коллекции на карточку источника по DocumentId — пересобирается при рендере источников.
    private Dictionary<int, (string Name, string Color)> _sourceBadges = new();

    public RagWindow(
        AnswerEngineManager engineManager,
        TranscriptFactory transcriptFactory,
        UiRecorder inputRecorder,
        RagDialogManager dialogManager,
        SourceLibraryManager? libraryManager,
        Func<EngineOption, string, Task<CliProbeResult>> probe,
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

        // RAG выключен — управлять источниками нечем, кнопку и выбор области прячем.
        SourcesButton.IsVisible = _libraryManager != null;
        ScopePanel.IsVisible = _libraryManager != null;

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

        // Старт: менеджер уже создал новый пустой диалог; прошлые диалоги доступны из History.
        ReloadHistory();
        RefreshScopeSelector();
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

    // ---------- Область поиска (All sources / коллекция / конкретные документы) ----------

    private static readonly IBrush DefaultScopeSquareBrush = Brush.Parse("#7a818d");

    /// <summary>
    /// Перестраивает «таблетку» области поиска и её меню (фильтр + коллекции с разворотом и чекбоксами
    /// документов) из библиотеки. Удалённая коллекция в сохранённом scope сбрасывается на всю библиотеку.
    /// </summary>
    internal void RefreshScopeSelector()
    {
        if (_libraryManager == null)
            return;

        var collections = _libraryManager.GetCollections();
        var entries = _libraryManager.GetEntries()
            .Where(e => e.DocumentId.HasValue)
            .ToArray();
        var totalDocs = entries.Length;
        var scope = _dialogManager.CurrentScope;

        // Активная коллекция исчезла (удалена) — сбрасываем scope на всю библиотеку.
        if (scope is { Kind: ScopeKind.Collection, CollectionId: { } cid } && collections.All(c => c.Id != cid))
        {
            _dialogManager.SetCollectionScope(null);
            scope = _dialogManager.CurrentScope;
        }

        // Выбранные документы, реально существующие в библиотеке (для таблетки и чекбоксов).
        var existingIds = entries.Select(e => e.DocumentId!.Value).ToHashSet();
        var selectedDocs = scope.Kind == ScopeKind.Documents
            ? scope.DocumentIds.Where(existingIds.Contains).ToHashSet()
            : new HashSet<int>();

        RefreshScopePill(scope, collections, totalDocs, selectedDocs);
        RefreshScopeMenu(collections, entries, scope, selectedDocs);
    }

    private void RefreshScopePill(SessionScope scope, BookCollectionSummary[] collections, int totalDocs,
        IReadOnlyCollection<int> selectedDocs)
    {
        switch (scope.Kind)
        {
            case ScopeKind.Collection when collections.FirstOrDefault(c => c.Id == scope.CollectionId) is { } active:
                ScopeSquare.IsVisible = true;
                ScopeSquare.Background = Brush.Parse(active.Color);
                ScopeLabelText.Text = Truncate(active.Name, 28);
                ScopeCountText.Text = DocsLabel(active.Count);
                break;
            case ScopeKind.Documents when selectedDocs.Count > 0:
                // У набора документов нет цвета коллекции — синий индикатор (мокап: blue when scoped to docs).
                ScopeSquare.IsVisible = true;
                ScopeSquare.Background = AccentBrush;
                ScopeLabelText.Text = SourcesLabel(selectedDocs.Count);
                ScopeCountText.Text = string.Empty;
                break;
            default:
                ScopeSquare.IsVisible = false;
                ScopeLabelText.Text = "All sources";
                ScopeCountText.Text = totalDocs > 0 ? DocsLabel(totalDocs) : string.Empty;
                break;
        }
    }

    private void RefreshScopeMenu(BookCollectionSummary[] collections, SourceLibraryEntry[] entries,
        SessionScope scope, HashSet<int> selectedDocs)
    {
        ScopeMenuPanel.Children.Clear();

        ScopeMenuPanel.Children.Add(BuildScopeFilterBox());

        var allActive = scope.Kind == ScopeKind.All;
        ScopeMenuPanel.Children.Add(BuildScopeAllRow(entries.Length, allActive));

        var filter = _scopeFilter.Trim();
        var filtering = filter.Length > 0;
        foreach (var collection in collections)
        {
            var docs = DocumentsInCollection(collection, entries);
            if (filtering)
            {
                docs = docs.Where(d => d.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (docs.Length == 0)
                    continue; // при активном фильтре прячем коллекции без совпадений
            }

            var collectionActive = scope.Kind == ScopeKind.Collection && scope.CollectionId == collection.Id;
            // Фильтр авто-разворачивает коллекции с совпадениями.
            var expanded = filtering || _expandedScopeCollections.Contains(collection.Id);
            ScopeMenuPanel.Children.Add(BuildScopeCollectionRow(collection, docs.Length, collectionActive, expanded));
            if (expanded)
                foreach (var doc in docs)
                    ScopeMenuPanel.Children.Add(BuildScopeDocRow(doc, collection.Color, selectedDocs.Contains(doc.DocumentId!.Value)));
        }

        ScopeMenuPanel.Children.Add(new Rectangle
        {
            Height = 1,
            Fill = Brush.Parse("#262a33"),
            Margin = new Thickness(4, 4, 4, 2),
        });
        ScopeMenuPanel.Children.Add(BuildScopeFooter(selectedDocs.Count));
    }

    private static SourceLibraryEntry[] DocumentsInCollection(BookCollectionSummary collection, SourceLibraryEntry[] entries)
    {
        // Дефолтная «General» — документы без пользовательского членства; иначе члены коллекции.
        return collection.IsDefault
            ? entries.Where(e => e.CollectionIds.Count == 0).ToArray()
            : entries.Where(e => e.CollectionIds.Contains(collection.Id)).ToArray();
    }

    private static string DocsLabel(int count) => $"{count} doc{(count == 1 ? string.Empty : "s")}";

    private static string SourcesLabel(int count) => $"{count} source{(count == 1 ? string.Empty : "s")}";

    private TextBox BuildScopeFilterBox()
    {
        var box = new TextBox
        {
            Text = _scopeFilter,
            Watermark = "Filter sources by name…",
            FontSize = 12,
            Margin = new Thickness(4, 4, 4, 6),
        };
        box.Classes.Add("scopeFilter");
        box.AttachedToVisualTree += (_, _) => box.Focus();
        box.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty)
                return;
            _scopeFilter = box.Text ?? string.Empty;
            RefreshScopeSelector();
        };
        return box;
    }

    private Border BuildScopeAllRow(int totalDocs, bool active)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

        var square = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(3),
            Background = DefaultScopeSquareBrush,
            Margin = new Thickness(20, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(square, 0);

        var label = new TextBlock
        {
            Text = "All sources",
            FontSize = 12.5,
            Foreground = Brush.Parse("#dfe3ea"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);

        var countText = new TextBlock
        {
            Text = totalDocs.ToString(CultureInfo.InvariantCulture),
            FontSize = 10.5,
            Foreground = RowMutedBrush,
            FontFamily = MonoFont,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 2);

        var check = new TextBlock
        {
            Text = active ? "✓" : string.Empty,
            FontSize = 12,
            Foreground = AccentBrush,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(check, 3);

        grid.Children.Add(square);
        grid.Children.Add(label);
        grid.Children.Add(countText);
        grid.Children.Add(check);

        return WrapScopeRow(grid, (_, _) =>
        {
            (ScopeButton.Flyout as Flyout)?.Hide();
            _dialogManager.SetCollectionScope(null);
            RefreshScopeSelector();
        });
    }

    private Border BuildScopeCollectionRow(BookCollectionSummary collection, int count, bool active, bool expanded)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };

        // Карета разворота — отдельная кликабельная зона (не меняет scope, только раскрывает документы).
        var caret = new Border
        {
            Width = 16,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = expanded ? "▾" : "▸",
                FontSize = 9,
                Foreground = Brush.Parse("#7a818d"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        caret.PointerPressed += (_, e) =>
        {
            e.Handled = true; // не пускаем событие в тело строки (иначе выберется коллекция)
            ToggleScopeCollectionExpanded(collection.Id);
        };
        Grid.SetColumn(caret, 0);

        var square = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(3),
            Background = Brush.Parse(collection.Color),
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(square, 1);

        var label = new TextBlock
        {
            Text = collection.Name,
            FontSize = 12.5,
            Foreground = Brush.Parse("#dfe3ea"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 2);

        var countText = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture),
            FontSize = 10.5,
            Foreground = RowMutedBrush,
            FontFamily = MonoFont,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 3);

        var check = new TextBlock
        {
            Text = active ? "✓" : string.Empty,
            FontSize = 12,
            Foreground = AccentBrush,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(check, 4);

        grid.Children.Add(caret);
        grid.Children.Add(square);
        grid.Children.Add(label);
        grid.Children.Add(countText);
        grid.Children.Add(check);

        return WrapScopeRow(grid, (_, _) =>
        {
            (ScopeButton.Flyout as Flyout)?.Hide();
            _dialogManager.SetCollectionScope(collection.Id);
            RefreshScopeSelector();
        });
    }

    private Border BuildScopeDocRow(SourceLibraryEntry entry, string color, bool selected)
    {
        var docId = entry.DocumentId!.Value;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };

        var checkbox = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(selected ? 0 : 1),
            BorderBrush = Brush.Parse("#3a4150"),
            Background = selected ? AccentBrush : Brushes.Transparent,
            Margin = new Thickness(20, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = selected ? "✓" : string.Empty,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#0b1220"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(checkbox, 0);

        var square = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(2),
            Background = Brush.Parse(color),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(square, 1);

        var label = new TextBlock
        {
            Text = entry.Title,
            FontSize = 12,
            Foreground = selected ? Brush.Parse("#dfe3ea") : Brush.Parse("#9aa1ad"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 2);

        grid.Children.Add(checkbox);
        grid.Children.Add(square);
        grid.Children.Add(label);

        // Тоггл документа сразу меняет scope, но меню НЕ закрываем — можно отметить несколько подряд.
        return WrapScopeRow(grid, (_, _) => ToggleScopeDocument(docId));
    }

    private Border BuildScopeFooter(int selectedCount)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        if (selectedCount > 0)
        {
            var selectedText = new TextBlock
            {
                Text = SelectedLabel(selectedCount),
                FontSize = 11.5,
                Foreground = AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(selectedText, 0);

            var clear = new TextBlock
            {
                Text = "Clear",
                FontSize = 11.5,
                Foreground = Brush.Parse("#9aa1ad"),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            clear.Classes.Add("scopeClear");
            var clearWrap = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(6, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = clear,
            };
            clearWrap.PointerPressed += (_, _) =>
            {
                _dialogManager.SetCollectionScope(null);
                RefreshScopeSelector();
            };
            Grid.SetColumn(clearWrap, 1);

            content.Children.Add(selectedText);
            content.Children.Add(clearWrap);
        }

        var library = BuildScopeLibraryRow();

        var stack = new StackPanel();
        if (selectedCount > 0)
            stack.Children.Add(content);
        stack.Children.Add(library);
        return new Border { Child = stack };
    }

    private static string SelectedLabel(int count) => $"{count} selected";

    private Border BuildScopeLibraryRow()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new TextBlock
        {
            Text = "⚙",
            FontSize = 12,
            Foreground = Brush.Parse("#9aa1ad"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Library",
            FontSize = 11.5,
            Foreground = Brush.Parse("#9aa1ad"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return WrapScopeRow(content, async (_, _) =>
        {
            (ScopeButton.Flyout as Flyout)?.Hide();
            await OpenLibraryAsync();
        });
    }

    private static Border WrapScopeRow(Control child, EventHandler<PointerPressedEventArgs> onClick)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = child,
        };
        border.Classes.Add("scopeRow");
        border.PointerPressed += onClick;
        return border;
    }

    private void ToggleScopeCollectionExpanded(int collectionId)
    {
        if (!_expandedScopeCollections.Add(collectionId))
            _expandedScopeCollections.Remove(collectionId);
        RefreshScopeSelector();
    }

    private void ToggleScopeDocument(int documentId)
    {
        var current = _dialogManager.CurrentScope;
        var ids = current.Kind == ScopeKind.Documents
            ? new List<int>(current.DocumentIds)
            : new List<int>();

        if (!ids.Remove(documentId))
            ids.Add(documentId);

        _dialogManager.SetDocumentScope(ids);
        RefreshScopeSelector();
    }

    private void ApplyRetrievalNotice(RagAskResult result)
    {
        // Область — коллекция без проиндексированных книг: явное сообщение вместо «no relevant sources».
        if (result.ScopeEmpty && !string.IsNullOrEmpty(result.ScopeName))
        {
            RetrievalNoticeText.Text = $"the '{result.ScopeName}' collection has no indexed sources yet";
            return;
        }

        RetrievalNoticeText.Text = result.Retrieval switch
        {
            RetrievalState.Skipped => "retrieval skipped · query too short",
            RetrievalState.NoneFound => "no relevant sources found",
            _ => string.Empty,
        };
    }

    private async void SourcesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await OpenLibraryAsync();
    }

    /// <summary>
    /// Открывает окно библиотеки источников и обновляет таблетку области поиска после закрытия.
    /// </summary>
    private async Task OpenLibraryAsync()
    {
        try
        {
            if (_libraryManager == null)
                return;

            // «Search only this source» из библиотеки: ограничить область поиска одним документом.
            await new SourceLibraryWindow(_libraryManager, docId => _dialogManager.SetDocumentScope(new[] { docId }))
                .ShowDialog(this);
            // Коллекции/состав/область могли измениться — пересобираем меню области поиска.
            RefreshScopeSelector();
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

        // Одна строка на диалог (отклонение от мокапа по требованию пользователя): повторный Send
        // обновляет строку текущего диалога, а не добавляет новую. Диалог встаёт под дату последней
        // активности — возобновлённый старый диалог поднимается под сегодняшний разделитель.
        var rows = new List<(DateTimeOffset At, int Order, RagSessionItem Session, RagExchangeView? LastExchange)>();
        foreach (var session in _dialogManager.GetSessions())
        {
            var exchanges = _dialogManager.GetExchanges(session.Id);
            rows.Add(exchanges.Length == 0
                ? (session.CreatedAt, 0, session, null)
                : (exchanges[^1].CreatedAt, exchanges[^1].Id, session, exchanges[^1]));
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

            HistoryPanel.Children.Add(BuildHistoryRow(row.Session, row.LastExchange));
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
        // Подсветка — текущий диалог: видно, куда уйдёт следующий Send.
        var selected = session.Id == _dialogManager.CurrentSession.Id;

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
            // Заголовок диалога — его название (первый вопрос); сниппет — последний ответ.
            Text = isEmpty
                ? "(empty dialog)"
                : Truncate(string.IsNullOrWhiteSpace(session.Title) ? exchange!.Question : session.Title!, 50),
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
        // Бейдж коллекции, к которой был ограничен ретрив обмена (по мокапу) — цветной квадрат + имя.
        if (exchange != null && !string.IsNullOrEmpty(exchange.ScopeName))
        {
            var badge = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(0, 3, 0, 0),
            };
            badge.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(2),
                Background = string.IsNullOrEmpty(exchange.ScopeColor) ? RowMutedBrush : Brush.Parse(exchange.ScopeColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            badge.Children.Add(new TextBlock
            {
                Text = Truncate(exchange.ScopeName, 24),
                FontSize = 10,
                Foreground = Brush.Parse("#8b929e"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            texts.Children.Add(badge);
        }
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
                await DeleteSessionAsync(session.Id);
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
            row.PointerPressed += (_, _) => OpenEmptySession(session);

        return row;
    }

    internal async Task DeleteSessionAsync(int sessionId)
    {
        await _dialogManager.DeleteSession(sessionId);
        if (_viewSessionId == sessionId)
            ReturnToLive();
        else
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
        ResetWorkbenchToLive();
    }

    /// <summary>
    /// Клик по строке «(empty dialog)»: пустой диалог становится текущим (Send продолжит его).
    /// </summary>
    internal void OpenEmptySession(RagSessionItem session)
    {
        _dialogManager.ResumeSession(session.Id);
        ResetWorkbenchToLive();
    }

    private void ResetWorkbenchToLive()
    {
        _viewExchange = null;
        _viewSessionId = null;
        ViewBanner.IsVisible = false;
        PromptBox.IsReadOnly = false;
        PromptHintText.Text = "editable";
        _livePrompt = string.Empty;
        _liveAnswer = string.Empty;
        _liveSources = Array.Empty<KnowledgeChunk>();
        PromptBox.Text = string.Empty;
        RenderAnswer(string.Empty);
        RenderSources(Array.Empty<KnowledgeChunk>());
        RetrievalNoticeText.Text = string.Empty;
        ReloadHistory();
        RefreshScopeSelector();
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
        RefreshScopeSelector();
        RetrievalNoticeText.Text = string.Empty;

        _viewExchange = exchange;
        _viewSessionId = session.Id;
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
        _viewSessionId = null;
        ViewBanner.IsVisible = false;
        PromptBox.IsReadOnly = false;
        PromptHintText.Text = "editable";
        PromptBox.Text = _livePrompt;
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
        // Ни ответ, ни источники не очищаем на время генерации — прогресс виден по статусу
        // «retrieving…/generating…»; Answer и Sources заменяются только готовым результатом (или ошибкой).
        // Раньше источники гасились здесь, и область моргала: список → пустая плашка → новый список.
        RetrievalNoticeText.Text = string.Empty;

        try
        {
            var progress = new UiPhaseProgress(phase =>
            {
                if (_state == UiState.Busy)
                    SetBusy(phase);
            });
            var result = await _dialogManager.Ask(question, _summaryCts.Token, progress);

            // Вопрос остаётся в поле (отличие от мокапа): очистка промпта после Send выглядела
            // как переключение на новый диалог. Workbench сбрасывается только кнопкой New dialog.
            _liveAnswer = result.Answer;
            _liveSources = result.Sources;
            _livePrompt = question;
            SetReady();
            if (_viewExchange != null)
            {
                // Ответ готов — возвращаемся из просмотра сохранённого обмена к активному диалогу,
                // чтобы свежий ответ был виден сразу.
                ReturnToLive();
            }
            else
            {
                RenderAnswer(result.Answer);
                RenderSources(result.Sources);
                PromptBox.Text = question;
            }

            ApplyRetrievalNotice(result);
            ReloadHistory();
            RefreshScopeSelector();
            UpdateContextIndicator();
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
        // Сброс выделения до замены контента: индексы селекции SelectableTextBlock
        // не клампятся при замене и могут указывать за пределы нового текста.
        AnswerBlock.ClearSelection();

        if (string.IsNullOrWhiteSpace(text))
        {
            AnswerBlock.Inlines = new InlineCollection();
            AnswerBlock.IsVisible = false;
            AnswerPlaceholder.IsVisible = true;
            return;
        }

        var inlines = new InlineCollection();
        foreach (var part in Regex.Split(text, @"(\[\d+\])"))
        {
            if (part.Length == 0)
                continue;

            if (Regex.IsMatch(part, @"^\[\d+\]$"))
            {
                inlines.Add(new Run(part)
                {
                    Foreground = AccentBrush,
                    FontWeight = FontWeight.SemiBold,
                    FontFamily = MonoFont,
                    FontSize = 12,
                });
            }
            else
            {
                inlines.Add(new Run(part));
            }
        }

        AnswerBlock.Inlines = inlines;
        AnswerPlaceholder.IsVisible = false;
        AnswerBlock.IsVisible = true;

        // Avalonia 11.3: после замены Inlines инвалидация measure обнуляет внутренние text runs,
        // и кадр, записанный до layout-прохода, рисует TextBlock пустым; новый рендер после layout
        // не планируется — ANSWER остаётся пустым до следующей перерисовки (гонка проявляется на
        // втором и последующих ответах при неизменных arrange-bounds; первый ответ всегда цел —
        // там контрол впервые становится видимым и проходит полный layout до рендера).
        // Лечение: синхронный layout-проход и принудительная перерисовка уже валидного состояния.
        AnswerBlock.UpdateLayout();
        AnswerBlock.InvalidateVisual();
    }

    // ---------- SOURCES ----------

    private void RenderSources(KnowledgeChunk[] chunks)
    {
        RebuildSourceBadges();
        SourcesPanel.Children.Clear();
        var any = chunks.Length > 0;
        SourcesEmptyPanel.IsVisible = !any;
        SourcesScroll.IsVisible = any;
        SourcesCountText.Text = any ? $"top-{chunks.Length}" : "none yet";
        for (var i = 0; i < chunks.Length; i++)
            SourcesPanel.Children.Add(BuildSourceCard(i + 1, chunks[i]));
    }

    /// <summary>
    /// Карта DocumentId → бейдж коллекции (первая пользовательская коллекция документа, иначе «General»)
    /// для карточек источников. Пересобирается перед каждым рендером источников.
    /// </summary>
    private void RebuildSourceBadges()
    {
        _sourceBadges = new Dictionary<int, (string, string)>();
        if (_libraryManager == null)
            return;

        var collections = _libraryManager.GetCollections().ToDictionary(c => c.Id);
        foreach (var entry in _libraryManager.GetEntries())
        {
            if (entry.DocumentId is not { } id)
                continue;

            var userCollection = entry.CollectionIds
                .Select(cid => collections.TryGetValue(cid, out var c) ? c : null)
                .FirstOrDefault(c => c is { IsDefault: false });
            _sourceBadges[id] = userCollection != null
                ? (userCollection.Name, userCollection.Color)
                : (SourceLibraryManager.DefaultCollectionName, SourceLibraryManager.DefaultCollectionColor);
        }
    }

    /// <summary>
    /// «Only this»: открывает новый диалог, ограниченный одним источником карточки, и ждёт нового вопроса
    /// (не переспрашивает прежний — пользователь вводит новый промпт в чистом диалоге).
    /// </summary>
    internal async Task OnlyThisSource(KnowledgeChunk chunk)
    {
        if (_libraryManager == null)
            return;

        // DocumentId известен у живых результатов; в легаси-снимках истории (0) — ищем по заголовку.
        var docId = chunk.DocumentId;
        if (docId == 0)
        {
            var match = _libraryManager.GetEntries()
                .FirstOrDefault(e => e.DocumentId.HasValue &&
                                     string.Equals(e.Title, chunk.SourceTitle, StringComparison.Ordinal));
            if (match?.DocumentId is not { } resolved)
            {
                Logger.Warn("Only this: source {0} not found in library", chunk.SourceTitle);
                return;
            }
            docId = resolved;
        }

        // Чистый новый диалог: пустой промпт, без старого ответа/источников.
        await _dialogManager.StartNewSession();
        ResetWorkbenchToLive();

        // Область нового диалога — только этот источник; pill обновляем после установки scope.
        _dialogManager.SetDocumentScope(new[] { docId });
        RefreshScopeSelector();

        PromptBox.Focus();
        UpdateSendState();
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
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(location);
        if (_libraryManager != null && chunk.DocumentId != 0 && _sourceBadges.TryGetValue(chunk.DocumentId, out var badge))
            titles.Children.Add(BuildSourceCollectionBadge(badge.Name, badge.Color));
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

        // «Only this» — переспросить текущий вопрос только по этому источнику (доступно при включённом RAG).
        if (_libraryManager != null)
        {
            var onlyBtn = new Button
            {
                Content = "Only this",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = AccentBrush,
                Padding = new Thickness(10, 4),
            };
            onlyBtn.Classes.Add("chip");
            ToolTip.SetTip(onlyBtn, "Re-ask this question against only this source");
            onlyBtn.Click += async (_, _) =>
            {
                try
                {
                    await OnlyThisSource(chunk);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Only this source failed");
                }
            };
            buttons.Children.Add(onlyBtn);
        }

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
            // Карточка всегда во всю ширину панели: длинный заголовок переносится в звёздной колонке
            // и не растягивает плашку шире остальных.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = inner,
        };
    }

    /// <summary>
    /// Компактный бейдж коллекции (цвет + имя) для карточки источника.
    /// </summary>
    private static Border BuildSourceCollectionBadge(string name, string color)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
        };
        content.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            Background = Brush.Parse(color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10.5,
            Foreground = Brush.Parse("#9aa1ad"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 160,
        });

        return new Border
        {
            Background = Brush.Parse("#13161c"),
            BorderBrush = Brush.Parse("#2a2e37"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(7, 2),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content,
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
