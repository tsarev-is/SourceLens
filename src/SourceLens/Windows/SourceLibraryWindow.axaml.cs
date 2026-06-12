using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SourceLens.Domain;

namespace SourceLens.Windows;

/// <summary>
/// Модальное окно «Source library» (по мокапу docs/RagWindow.dc.html): список проиндексированных
/// источников с прогрессом индексации, добавление через файл-пикер и удаление из индекса.
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
    private static readonly FontFamily MonoFont = new("Consolas,Menlo,monospace");

    /// <summary>
    /// Контролы одной карточки — шов для headless-тестов.
    /// </summary>
    internal sealed record CardHandles(
        SourceLibraryEntry Entry,
        TextBlock Meta,
        TextBlock Pill,
        Button Remove,
        ProgressBar? Progress);

    private readonly SourceLibraryManager _manager;
    private readonly List<CardHandles> _cards = new();

    public SourceLibraryWindow(SourceLibraryManager manager)
    {
        _manager = manager;

        InitializeComponent();

        _manager.Changed += OnLibraryChanged;
        Refresh();
    }

    internal IReadOnlyList<CardHandles> Cards => _cards;

    protected override void OnClosed(EventArgs e)
    {
        _manager.Changed -= OnLibraryChanged;
        base.OnClosed(e);
    }

    private void OnLibraryChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    internal void Refresh()
    {
        var entries = _manager.GetEntries();

        DocsPanel.Children.Clear();
        _cards.Clear();
        foreach (var entry in entries)
            DocsPanel.Children.Add(BuildCard(entry));

        EmptyText.IsVisible = entries.Length == 0;

        var totalChunks = entries.Where(e => !e.Indexing).Sum(e => e.ChunkCount);
        LibraryMetaText.Text =
            $"{entries.Length} source{(entries.Length == 1 ? string.Empty : "s")} · " +
            $"{totalChunks.ToString("N0", CultureInfo.InvariantCulture)} chunks indexed";
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
            var percent = new TextBlock
            {
                Text = $"{entry.ProgressPercent}%",
                FontSize = 10.5,
                Foreground = ProgressTextBrush,
                FontFamily = MonoFont,
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(percent, 1);

            var progressRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 8, 0, 0),
            };
            progressRow.Children.Add(progressBar);
            progressRow.Children.Add(percent);
            texts.Children.Add(progressRow);
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

        _cards.Add(new CardHandles(entry, meta, pill, remove, progressBar));

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

    internal async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        await Task.WhenAll(paths.Select(_manager.AddSourceAsync));
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
                await AddFilesAsync(paths);
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
