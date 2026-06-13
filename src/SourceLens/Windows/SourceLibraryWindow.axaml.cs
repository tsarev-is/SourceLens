using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SourceLens.Domain;
using SourceLens.Domain.Entities;

namespace SourceLens.Windows;

/// <summary>
/// Модальное окно «Source library» (по мокапу docs/RagWindow.dc.html): слева — коллекции источников
/// (создание/удаление/выбор), справа — отфильтрованный по коллекции список проиндексированных источников
/// с прогрессом индексации, добавление через файл-пикер, удаление из индекса и членство в коллекциях.
/// </summary>
public partial class SourceLibraryWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly IBrush CardBackgroundBrush = Brush.Parse("#15181d");
    private static readonly IBrush CardBorderBrush = Brush.Parse("#262a33");
    private static readonly IBrush IconBackgroundBrush = Brush.Parse("#101216");
    private static readonly IBrush IconBorderBrush = Brush.Parse("#2f3441");
    private static readonly IBrush AccentBrush = Brush.Parse("#6aa6ff");
    private static readonly IBrush TitleBrush = Brush.Parse("#dfe3ea");
    private static readonly IBrush MetaBrush = Brush.Parse("#6b7280");
    private static readonly IBrush ProgressTextBrush = Brush.Parse("#9aa1ad");
    private static readonly IBrush ProgressTrackBrush = Brush.Parse("#262a33");
    private static readonly IBrush IndexingFgBrush = Brush.Parse("#e3b341");
    private static readonly IBrush IndexingBgBrush = Brush.Parse("#1FE3B341");
    private static readonly IBrush IndexingBorderBrush = Brush.Parse("#59E3B341");
    private static readonly IBrush IndexedFgBrush = Brush.Parse("#4ec98a");
    private static readonly IBrush IndexedBgBrush = Brush.Parse("#1F4EC98A");
    private static readonly IBrush IndexedBorderBrush = Brush.Parse("#594EC98A");
    private static readonly IBrush RemoveIdleBrush = Brush.Parse("#3d4350");
    private static readonly IBrush MutedBrush = Brush.Parse("#5b626e");
    private static readonly IBrush SelectedRowBrush = Brush.Parse("#1F6AA6FF");
    private static readonly FontFamily MonoFont = new("Consolas,Menlo,monospace");

    /// <summary>
    /// Контролы одной карточки — шов для headless-тестов.
    /// </summary>
    internal sealed record CardHandles(
        SourceLibraryEntry Entry,
        TextBlock Meta,
        TextBlock Pill,
        Button Remove,
        ProgressBar? Progress,
        TextBlock? Percent,
        Button? Members);

    /// <summary>
    /// Контролы строки коллекции в сайдбаре — шов для headless-тестов.
    /// </summary>
    internal sealed record CollectionHandles(int? CollectionId, string Name, int Count, Border Row, Button? Delete);

    private readonly SourceLibraryManager _manager;
    private readonly List<CardHandles> _cards = new();
    private readonly List<CollectionHandles> _collectionRows = new();

    // Выбранная в сайдбаре коллекция (null — «All sources», агрегат всей библиотеки).
    private int? _selectedCollectionId;
    private BookCollectionSummary[] _collections = Array.Empty<BookCollectionSummary>();

    public SourceLibraryWindow(SourceLibraryManager manager)
    {
        _manager = manager;

        InitializeComponent();

        _manager.Changed += OnLibraryChanged;
        _manager.IndexingProgress += OnIndexingProgress;
        Refresh();
    }

    internal IReadOnlyList<CardHandles> Cards => _cards;

    internal IReadOnlyList<CollectionHandles> CollectionRows => _collectionRows;

    /// <summary>
    /// Шов для headless-тестов: выбрать коллекцию в сайдбаре (null — «All sources»).
    /// </summary>
    internal void SelectCollectionForTest(int? collectionId)
    {
        _selectedCollectionId = collectionId;
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _manager.Changed -= OnLibraryChanged;
        _manager.IndexingProgress -= OnIndexingProgress;
        base.OnClosed(e);
    }

    private void OnLibraryChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>
    /// Тик прогресса индексации: двигаем прогресс-бар и процент нужной карточки на месте, без полного
    /// Refresh — иначе список книг и сайдбар коллекций перестраиваются на каждом чанке и интерфейс моргает.
    /// </summary>
    private void OnIndexingProgress(string fullPath, int percent)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyIndexingProgress(fullPath, percent);
        else
            Dispatcher.UIThread.Post(() => ApplyIndexingProgress(fullPath, percent));
    }

    private void ApplyIndexingProgress(string fullPath, int percent)
    {
        foreach (var card in _cards)
        {
            if (card.Progress == null || !card.Entry.Indexing)
                continue;
            if (!string.Equals(Path.GetFullPath(card.Entry.FilePath), fullPath, StringComparison.Ordinal))
                continue;

            card.Progress.Value = percent;
            if (card.Percent != null)
                card.Percent.Text = $"{percent}%";
            break;
        }
    }

    internal void Refresh()
    {
        _collections = _manager.GetCollections();
        // Выбранная коллекция могла быть удалена — возвращаемся к «All sources».
        if (_selectedCollectionId.HasValue && _collections.All(c => c.Id != _selectedCollectionId.Value))
            _selectedCollectionId = null;

        var entries = _manager.GetEntries();

        RefreshCollections();
        RefreshDocuments(entries);

        // Метаданные шапки — по всей библиотеке (не по фильтру).
        var totalSources = entries.Length;
        var totalChunks = entries.Where(e => !e.Indexing).Sum(e => e.ChunkCount);
        LibraryMetaText.Text =
            $"{totalSources} source{(totalSources == 1 ? string.Empty : "s")} · " +
            $"{_collections.Length} collection{(_collections.Length == 1 ? string.Empty : "s")} · " +
            $"{totalChunks.ToString("N0", CultureInfo.InvariantCulture)} chunks indexed";
    }

    // ---------- Сайдбар коллекций ----------

    private void RefreshCollections()
    {
        CollectionsPanel.Children.Clear();
        _collectionRows.Clear();

        var totalDocs = _manager.GetEntries().Count(e => e.DocumentId.HasValue);
        CollectionsPanel.Children.Add(BuildCollectionRow(null, "All sources", null, totalDocs, isDefault: false));
        foreach (var collection in _collections)
            CollectionsPanel.Children.Add(BuildCollectionRow(collection.Id, collection.Name, collection.Color,
                collection.Count, collection.IsDefault));
    }

    private Border BuildCollectionRow(int? collectionId, string name, string? color, int count, bool isDefault)
    {
        var selected = _selectedCollectionId == collectionId;

        var square = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(3),
            Background = color != null ? Brush.Parse(color) : MutedBrush,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = collectionId != null,
        };
        Grid.SetColumn(square, 0);

        var label = new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            Foreground = selected ? Brush.Parse("#dfe3ea") : Brush.Parse("#c3c8d1"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);

        var countText = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture),
            FontSize = 10.5,
            Foreground = MutedBrush,
            FontFamily = MonoFont,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 2);

        Button? delete = null;
        // Удалять можно только пользовательские коллекции (не «All sources», не дефолтную).
        if (collectionId != null && !isDefault)
        {
            delete = new Button
            {
                Content = "✕",
                FontSize = 11,
                Width = 17,
                Height = 17,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = RemoveIdleBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            delete.Classes.Add("collDelete");
            ToolTip.SetTip(delete, "Delete collection");
            var id = collectionId.Value;
            delete.Click += async (_, _) =>
            {
                try
                {
                    await _manager.DeleteCollection(id);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Delete collection failed");
                }
            };
            Grid.SetColumn(delete, 3);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(square);
        grid.Children.Add(label);
        grid.Children.Add(countText);
        if (delete != null)
            grid.Children.Add(delete);

        var row = new Border
        {
            Background = selected ? SelectedRowBrush : Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        row.Classes.Add("collectionRow");
        row.PointerPressed += (_, _) =>
        {
            _selectedCollectionId = collectionId;
            Refresh();
        };

        _collectionRows.Add(new CollectionHandles(collectionId, name, count, row, delete));
        return row;
    }

    private void NewCollectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NewCollectionButton.IsVisible = false;
        NewCollectionForm.IsVisible = true;
        NewCollectionBox.Text = string.Empty;
        NewCollectionBox.Focus();
    }

    private void CancelCollectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NewCollectionForm.IsVisible = false;
        NewCollectionButton.IsVisible = true;
    }

    private async void NewCollectionBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ConfirmNewCollectionAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelCollectionButton_OnClick(sender, new RoutedEventArgs());
        }
    }

    private async void CreateCollectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await ConfirmNewCollectionAsync();
    }

    internal async Task ConfirmNewCollectionAsync()
    {
        var name = (NewCollectionBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
            return;

        try
        {
            var id = await _manager.CreateCollection(name);
            NewCollectionForm.IsVisible = false;
            NewCollectionButton.IsVisible = true;
            // Сразу выбираем новую коллекцию (как в мокапе).
            if (id != 0)
                _selectedCollectionId = id;
            Refresh();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Create collection failed");
        }
    }

    // ---------- Список документов ----------

    private void RefreshDocuments(SourceLibraryEntry[] entries)
    {
        var visible = entries.Where(MatchesSelectedCollection).ToArray();

        DocsPanel.Children.Clear();
        _cards.Clear();
        foreach (var entry in visible)
            DocsPanel.Children.Add(BuildCard(entry));

        var selected = _selectedCollectionId.HasValue
            ? _collections.FirstOrDefault(c => c.Id == _selectedCollectionId.Value)
            : null;
        DocsHeaderText.Text = selected == null
            ? $"All sources · {visible.Length} source{(visible.Length == 1 ? string.Empty : "s")}"
            : $"{selected.Name} · {visible.Length} source{(visible.Length == 1 ? string.Empty : "s")}";

        EmptyText.IsVisible = visible.Length == 0;
        EmptyText.Text = _selectedCollectionId == null
            ? "No sources indexed yet"
            : "No sources in this collection yet.";
    }

    private bool MatchesSelectedCollection(SourceLibraryEntry entry)
    {
        if (_selectedCollectionId == null)
            return true;

        var selected = _collections.FirstOrDefault(c => c.Id == _selectedCollectionId.Value);
        // Дефолтная коллекция (General) — книги без пользовательского членства.
        if (selected is { IsDefault: true })
            return entry.CollectionIds.Count == 0;

        return entry.CollectionIds.Contains(_selectedCollectionId.Value);
    }

    private Border BuildCard(SourceLibraryEntry entry)
    {
        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = IconBackgroundBrush,
            BorderBrush = IconBorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 1, 11, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M14 3 H7 A2 2 0 0 0 5 5 V19 A2 2 0 0 0 7 21 H17 A2 2 0 0 0 19 19 V8 Z M14 3 V8 H19"),
                Stroke = AccentBrush,
                StrokeThickness = 2,
                StrokeJoin = PenLineJoin.Round,
                StrokeLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Width = 15,
                Height = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = TitleBrush,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
        };
        var meta = new TextBlock
        {
            Text = entry.Indexing
                ? "Indexing… chunking + embedding"
                : $"{KindOf(entry.FilePath)} · {entry.ChunkCount.ToString("N0", CultureInfo.InvariantCulture)} chunks",
            FontSize = 11,
            Foreground = MetaBrush,
            FontFamily = MonoFont,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(meta);

        ProgressBar? progressBar = null;
        TextBlock? percentText = null;
        if (entry.Indexing)
        {
            progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = entry.ProgressPercent,
                Height = 5,
                MinWidth = 0,
                CornerRadius = new CornerRadius(3),
                Foreground = AccentBrush,
                Background = ProgressTrackBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            percentText = new TextBlock
            {
                Text = $"{entry.ProgressPercent}%",
                FontSize = 10.5,
                Foreground = ProgressTextBrush,
                FontFamily = MonoFont,
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(percentText, 1);

            var progressRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 8, 0, 0),
            };
            progressRow.Children.Add(progressBar);
            progressRow.Children.Add(percentText);
            texts.Children.Add(progressRow);
        }

        // Членство в коллекциях — только для проиндексированных книг.
        Button? membersButton = null;
        if (!entry.Indexing && entry.DocumentId is { } docId)
        {
            var membership = BuildMembershipControl(docId, entry.CollectionIds, out membersButton);
            texts.Children.Add(membership);
        }

        Grid.SetColumn(texts, 1);

        var pill = new TextBlock
        {
            Text = entry.Indexing ? "Indexing" : "Indexed",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = entry.Indexing ? IndexingFgBrush : IndexedFgBrush,
        };
        var pillBorder = new Border
        {
            Background = entry.Indexing ? IndexingBgBrush : IndexedBgBrush,
            BorderBrush = entry.Indexing ? IndexingBorderBrush : IndexedBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(9, 2),
            Margin = new Thickness(10, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = pill,
        };
        Grid.SetColumn(pillBorder, 2);

        var remove = new Button
        {
            Content = "✕",
            FontSize = 12,
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 1, 0, 0),
            Background = Brushes.Transparent,
            Foreground = RemoveIdleBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = !entry.Indexing && entry.DocumentId != null,
        };
        remove.Classes.Add("cardDelete");
        ToolTip.SetTip(remove, "Remove source");
        remove.Click += async (_, _) =>
        {
            try
            {
                if (entry.DocumentId != null)
                    await RemoveDocumentAsync(entry.DocumentId.Value);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Remove source failed");
            }
        };
        Grid.SetColumn(remove, 3);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        grid.Children.Add(pillBorder);
        grid.Children.Add(remove);

        _cards.Add(new CardHandles(entry, meta, pill, remove, progressBar, percentText, membersButton));

        return new Border
        {
            Background = CardBackgroundBrush,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 11),
            Child = grid,
        };
    }

    /// <summary>
    /// Контрол членства карточки: бейджи текущих коллекций (или «General») + кнопка-дропдаун
    /// с чек-листом пользовательских коллекций (many-to-many: переключение членства).
    /// </summary>
    private Control BuildMembershipControl(int documentId, IReadOnlyList<int> currentIds, out Button button)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var userCollections = _collections.Where(c => !c.IsDefault).ToArray();
        var current = userCollections.Where(c => currentIds.Contains(c.Id)).ToArray();

        if (current.Length == 0)
        {
            row.Children.Add(BuildMembershipBadge(SourceLibraryManager.DefaultCollectionName,
                SourceLibraryManager.DefaultCollectionColor));
        }
        else
        {
            foreach (var collection in current)
                row.Children.Add(BuildMembershipBadge(collection.Name, collection.Color));
        }

        var menu = new StackPanel { MinWidth = 190 };
        menu.Children.Add(new TextBlock
        {
            Text = "Collections",
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.6,
            Foreground = MutedBrush,
            Margin = new Thickness(8, 6, 8, 5),
        });
        if (userCollections.Length == 0)
        {
            menu.Children.Add(new TextBlock
            {
                Text = "No collections yet",
                FontSize = 11.5,
                Foreground = MutedBrush,
                Margin = new Thickness(8, 2, 8, 6),
            });
        }
        else
        {
            foreach (var collection in userCollections)
                menu.Children.Add(BuildMembershipMenuRow(documentId, collection, currentIds.Contains(collection.Id)));
        }

        button = new Button
        {
            Content = "Collections ▾",
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("memberChip");
        ToolTip.SetTip(button, "Add to or remove from collections");
        button.Flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new Border
            {
                Background = Brush.Parse("#181b21"),
                BorderBrush = Brush.Parse("#2f3441"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(5),
                Child = menu,
            },
        };
        row.Children.Add(button);

        return row;
    }

    private static Border BuildMembershipBadge(string name, string color)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
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
            FontSize = 11,
            Foreground = Brush.Parse("#9aa1ad"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
        });
        return new Border
        {
            Background = Brush.Parse("#13161c"),
            BorderBrush = Brush.Parse("#2a2e37"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
    }

    private Border BuildMembershipMenuRow(int documentId, BookCollectionSummary collection, bool member)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };

        var check = new TextBlock
        {
            Text = member ? "✓" : string.Empty,
            FontSize = 12,
            Width = 14,
            Foreground = AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(check, 0);

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

        grid.Children.Add(check);
        grid.Children.Add(square);
        grid.Children.Add(label);

        var border = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        border.Classes.Add("memberRow");
        border.PointerPressed += async (_, _) =>
        {
            try
            {
                await ToggleMembershipAsync(documentId, collection.Id, member);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Toggle collection membership failed");
            }
        };
        return border;
    }

    internal async Task ToggleMembershipAsync(int documentId, int collectionId, bool currentlyMember)
    {
        if (currentlyMember)
            await _manager.RemoveFromCollection(documentId, collectionId);
        else
            await _manager.AddToCollection(documentId, collectionId);
    }

    private static string KindOf(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "PDF",
            ".epub" => "EPUB",
            ".txt" => "Text",
            ".md" => "Markdown",
            _ => extension.TrimStart('.').ToUpperInvariant(),
        };
    }

    internal async Task RemoveDocumentAsync(int documentId)
    {
        await _manager.RemoveDocumentAsync(documentId);
        Refresh();
    }

    internal async Task AddFilesAsync(IReadOnlyList<string> paths, int? collectionId = null)
    {
        await Task.WhenAll(paths.Select(p => _manager.AddSourceAsync(p, collectionId)));
    }

    /// <summary>
    /// Целевая коллекция загрузки — выбранная в сайдбаре пользовательская коллекция. «All sources» и
    /// дефолтная General дают null (документ без явного членства и так попадёт в General).
    /// </summary>
    internal int? UploadTargetCollectionId()
    {
        if (!_selectedCollectionId.HasValue)
            return null;

        var selected = _collections.FirstOrDefault(c => c.Id == _selectedCollectionId.Value);
        return selected is { IsDefault: false } ? _selectedCollectionId : null;
    }

    private async void AddSourcesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add sources",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Documents (PDF, EPUB, TXT, MD)")
                    {
                        Patterns = new[] { "*.pdf", "*.epub", "*.txt", "*.md" },
                    },
                },
            });

            var paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToArray();
            if (paths.Length > 0)
                await AddFilesAsync(paths, UploadTargetCollectionId());
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "Add sources failed");
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
