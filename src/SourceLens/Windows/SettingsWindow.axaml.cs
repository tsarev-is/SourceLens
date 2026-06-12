using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SourceLens.Configuration;
using SourceLens.Domain;
using SourceLens.Integrations.Cli;

namespace SourceLens.Windows;

/// <summary>
/// Модальное окно настроек: выбор движка ответов (карточки-радио по мокапу),
/// статус CLI через probe и выбор модели. Выбор применяется сразу и персистится.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly IBrush AccentBrush = Brush.Parse("#6aa6ff");
    private static readonly IBrush CardBorderBrush = Brush.Parse("#262a33");
    private static readonly IBrush CardBackgroundBrush = Brush.Parse("#15181d");
    private static readonly IBrush CardSelectedBorderBrush = Brush.Parse("#6B6AA6FF");
    private static readonly IBrush CardSelectedBackgroundBrush = Brush.Parse("#0F6AA6FF");
    private static readonly IBrush RadioIdleBrush = Brush.Parse("#3a4150");
    private static readonly IBrush DetectedBrush = Brush.Parse("#4ec98a");
    private static readonly IBrush NotFoundBrush = Brush.Parse("#e5534b");
    private static readonly IBrush CheckingBrush = Brush.Parse("#7a818d");
    private static readonly FontFamily MonoFont = new("Consolas,Menlo,monospace");

    private sealed record CardTexts(string Name, string Sub, string Description, string CliName, string Help, string Icon, string IconColor);

    private sealed class EngineCard
    {
        public required EngineOption Option { get; init; }

        public required CardTexts Texts { get; init; }

        public required Border Root { get; init; }

        public required Border RadioOuter { get; init; }

        public required Ellipse RadioDot { get; init; }

        public required Border Badge { get; init; }

        public required TextBlock BadgeText { get; init; }

        public required Border Details { get; init; }

        public required Ellipse CliDot { get; init; }

        public required TextBlock CliStatusText { get; init; }

        public required Border CliTag { get; init; }

        public required TextBlock CliTagText { get; init; }

        public required ComboBox ModelBox { get; init; }
    }

    private readonly AnswerEngineManager _engineManager;
    private readonly EngineOption[] _engineOptions;
    private readonly Func<EngineOption, Task<CliProbeResult>> _probe;
    private readonly Action? _engineChanged;
    private readonly Dictionary<string, string> _models = new();
    private readonly List<EngineCard> _cards = new();
    private bool _suppressModelEvents;

    public SettingsWindow(
        AnswerEngineManager engineManager,
        EngineOption[] engineOptions,
        Func<EngineOption, Task<CliProbeResult>> probe,
        Action? engineChanged = null)
    {
        _engineManager = engineManager;
        _engineOptions = engineOptions;
        _probe = probe;
        _engineChanged = engineChanged;

        InitializeComponent();

        foreach (var option in _engineOptions)
        {
            _models[option.Provider] = option.Provider == _engineManager.Provider && !string.IsNullOrWhiteSpace(_engineManager.Model)
                ? _engineManager.Model
                : option.DefaultModel;

            var card = BuildCard(option);
            _cards.Add(card);
            CardsPanel.Children.Add(card.Root);
        }

        RefreshCards();
        _ = RunProbesAsync();
    }

    private static CardTexts TextsFor(string provider)
    {
        return provider switch
        {
            EngineSettings.CodexProvider => new CardTexts(
                "ChatGPT", "via Codex CLI", "OpenAI Codex agent · uses your ChatGPT sign-in", "Codex CLI",
                "SourceLens routes prompts through your local Codex CLI, which uses your ChatGPT sign-in — no API key needed.",
                "</>", "#6aa6ff"),
            EngineSettings.ClaudeProvider => new CardTexts(
                "Claude", "via Claude CLI", "Anthropic Claude agent · uses your Claude sign-in", "Claude CLI",
                "SourceLens routes prompts through your local Claude CLI, which uses your Claude sign-in — no API key needed.",
                "✳", "#c08457"),
            _ => new CardTexts(provider, $"via {provider} CLI", $"{provider} CLI agent", $"{provider} CLI", string.Empty, "·", "#6aa6ff"),
        };
    }

    private EngineCard BuildCard(EngineOption option)
    {
        var texts = TextsFor(option.Provider);

        var radioDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = AccentBrush,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var radioOuter = new Border
        {
            Width = 17,
            Height = 17,
            CornerRadius = new CornerRadius(8.5),
            BorderThickness = new Thickness(1.5),
            BorderBrush = RadioIdleBrush,
            Margin = new Thickness(0, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = radioDot,
        };

        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = Brush.Parse("#101216"),
            BorderBrush = Brush.Parse("#2f3441"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = texts.Icon,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse(texts.IconColor),
                FontFamily = MonoFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(icon, 1);

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock
        {
            Text = texts.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#e6e8ec"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = texts.Sub,
            FontSize = 10.5,
            FontWeight = FontWeight.Medium,
            Foreground = Brush.Parse("#7a818d"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(nameRow);
        titles.Children.Add(new TextBlock
        {
            Text = texts.Description,
            FontSize = 11.5,
            Foreground = Brush.Parse("#7a818d"),
            Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(titles, 2);

        var badgeText = new TextBlock
        {
            Text = "Checking…",
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = CheckingBrush,
        };
        var badge = new Border
        {
            Padding = new Thickness(9, 2),
            CornerRadius = new CornerRadius(20),
            Background = Brush.Parse("#1F7A818D"),
            BorderBrush = Brush.Parse("#597A818D"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0),
            Child = badgeText,
        };
        Grid.SetColumn(badge, 3);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
        };
        header.Children.Add(radioOuter);
        header.Children.Add(icon);
        header.Children.Add(titles);
        header.Children.Add(badge);

        // --- развёрнутая часть выбранной карточки ---

        var cliDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = CheckingBrush,
            Margin = new Thickness(0, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var cliStatusText = new TextBlock
        {
            Text = "checking…",
            FontSize = 11,
            Foreground = Brush.Parse("#6b7280"),
            FontFamily = MonoFont,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var cliTitles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        cliTitles.Children.Add(new TextBlock
        {
            Text = texts.CliName,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#cdd3dc"),
        });
        cliTitles.Children.Add(cliStatusText);
        Grid.SetColumn(cliTitles, 1);

        var cliTagText = new TextBlock
        {
            Text = "Checking…",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = CheckingBrush,
        };
        var cliTag = new Border
        {
            Padding = new Thickness(8, 2),
            CornerRadius = new CornerRadius(20),
            Background = Brush.Parse("#1F7A818D"),
            BorderBrush = Brush.Parse("#597A818D"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0),
            Child = cliTagText,
        };
        Grid.SetColumn(cliTag, 2);

        var cliGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        cliGrid.Children.Add(cliDot);
        cliGrid.Children.Add(cliTitles);
        cliGrid.Children.Add(cliTag);
        var cliBox = new Border
        {
            Background = Brush.Parse("#101216"),
            BorderBrush = Brush.Parse("#23262e"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = cliGrid,
        };

        // До ответа CLI в списке только текущая модель; каталог доступных заменит его в ApplyDiscoveredModels.
        var model = _models[option.Provider];
        var modelBox = new ComboBox
        {
            ItemsSource = string.IsNullOrEmpty(model) ? Array.Empty<string>() : new[] { model },
            SelectedItem = string.IsNullOrEmpty(model) ? null : model,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Разблокируется, когда probe подтвердит CLI; список заменяется на модели, доступные в самом CLI.
            IsEnabled = false,
        };
        modelBox.Classes.Add("dark");

        var detailsStack = new StackPanel();
        detailsStack.Children.Add(cliBox);
        detailsStack.Children.Add(new TextBlock
        {
            Text = "Model",
            FontSize = 11,
            Foreground = Brush.Parse("#9aa1ad"),
            Margin = new Thickness(0, 13, 0, 6),
        });
        detailsStack.Children.Add(modelBox);
        if (!string.IsNullOrEmpty(texts.Help))
        {
            detailsStack.Children.Add(new TextBlock
            {
                Text = texts.Help,
                FontSize = 11,
                LineHeight = 16,
                Foreground = Brush.Parse("#5f6672"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 13, 0, 0),
            });
        }

        var details = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(0, 14, 0, 0),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = detailsStack,
        };

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(details);

        var root = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 13),
            BorderThickness = new Thickness(1),
            BorderBrush = CardBorderBrush,
            Background = CardBackgroundBrush,
            Child = content,
        };

        var card = new EngineCard
        {
            Option = option,
            Texts = texts,
            Root = root,
            RadioOuter = radioOuter,
            RadioDot = radioDot,
            Badge = badge,
            BadgeText = badgeText,
            Details = details,
            CliDot = cliDot,
            CliStatusText = cliStatusText,
            CliTag = cliTag,
            CliTagText = cliTagText,
            ModelBox = modelBox,
        };

        header.PointerPressed += (_, _) => Select(card);
        modelBox.SelectionChanged += (_, _) =>
        {
            if (_suppressModelEvents)
                return;

            var selected = modelBox.SelectedItem as string ?? string.Empty;
            _models[option.Provider] = selected;
            if (_engineManager.Provider == option.Provider && _engineManager.Model != selected)
            {
                _engineManager.Switch(option.Provider, selected);
                _engineChanged?.Invoke();
            }
        };

        return card;
    }

    internal ComboBox? ModelBoxFor(string provider)
    {
        return _cards.FirstOrDefault(c => c.Option.Provider == provider)?.ModelBox;
    }

    internal void SelectProvider(string provider)
    {
        var card = _cards.FirstOrDefault(c => c.Option.Provider == provider);
        if (card != null)
            Select(card);
    }

    private void Select(EngineCard card)
    {
        var model = _models[card.Option.Provider];
        if (_engineManager.Provider != card.Option.Provider || _engineManager.Model != model)
        {
            _engineManager.Switch(card.Option.Provider, model);
            _engineChanged?.Invoke();
        }

        RefreshCards();
    }

    private void RefreshCards()
    {
        _suppressModelEvents = true;
        try
        {
            foreach (var card in _cards)
            {
                var selected = card.Option.Provider == _engineManager.Provider;
                card.Root.BorderBrush = selected ? CardSelectedBorderBrush : CardBorderBrush;
                card.Root.Background = selected ? CardSelectedBackgroundBrush : CardBackgroundBrush;
                card.RadioOuter.BorderBrush = selected ? AccentBrush : RadioIdleBrush;
                card.RadioDot.IsVisible = selected;
                card.Details.IsVisible = selected;
                var model = _models[card.Option.Provider];
                card.ModelBox.SelectedItem = string.IsNullOrEmpty(model) ? null : model;
            }
        }
        finally
        {
            _suppressModelEvents = false;
        }
    }

    private async Task RunProbesAsync()
    {
        foreach (var card in _cards)
        {
            try
            {
                var result = await _probe(card.Option);
                ApplyProbe(card, result);
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "CLI probe failed for '{0}'", card.Option.BinaryPath);
                ApplyProbe(card, new CliProbeResult(false, string.Empty, card.Option.BinaryPath));
            }
        }
    }

    private void ApplyProbe(EngineCard card, CliProbeResult result)
    {
        if (result.Found)
        {
            var name = System.IO.Path.GetFileName(result.ResolvedPath);
            ApplyTag(card.Badge, card.BadgeText, "Detected", DetectedBrush, "#1F4EC98A", "#594EC98A");
            ApplyTag(card.CliTag, card.CliTagText, "Detected", DetectedBrush, "#1F4EC98A", "#594EC98A");
            card.CliDot.Fill = DetectedBrush;
            card.CliStatusText.Text = string.IsNullOrWhiteSpace(result.Version)
                ? $"{name} · {result.ResolvedPath}"
                : $"{name} v{result.Version} · {result.ResolvedPath}";
            ApplyDiscoveredModels(card, result.Models);
            card.ModelBox.IsEnabled = true;
        }
        else
        {
            ApplyTag(card.Badge, card.BadgeText, "Not found", NotFoundBrush, "#1FE5534B", "#59E5534B");
            ApplyTag(card.CliTag, card.CliTagText, "Not found", NotFoundBrush, "#1FE5534B", "#59E5534B");
            card.CliDot.Fill = NotFoundBrush;
            card.CliStatusText.Text = $"not found · looked for: {card.Option.BinaryPath}";
            card.ModelBox.IsEnabled = false;
        }
    }

    /// <summary>
    /// Заменяет список моделей карточки на модели, которые реально позволяет выбрать CLI.
    /// Если сохранённая модель в CLI недоступна — откатывается на default (или первую доступную)
    /// и, для активного движка, сразу персистит выбор.
    /// </summary>
    private void ApplyDiscoveredModels(EngineCard card, IReadOnlyList<string> discovered)
    {
        if (discovered.Count == 0)
            return; // CLI не сообщил список — остаёмся на fallback-списке из конфигурации

        var provider = card.Option.Provider;
        var current = _models[provider];
        var model = discovered.Contains(current)
            ? current
            : discovered.Contains(card.Option.DefaultModel) ? card.Option.DefaultModel : discovered[0];

        _suppressModelEvents = true;
        try
        {
            card.ModelBox.ItemsSource = discovered;
            card.ModelBox.SelectedItem = model;
        }
        finally
        {
            _suppressModelEvents = false;
        }

        if (model == current)
            return;

        Logger.Info("Model '{0}' is not available in {1} CLI, falling back to '{2}'", current, provider, model);
        _models[provider] = model;
        if (_engineManager.Provider == provider && _engineManager.Model != model)
        {
            _engineManager.Switch(provider, model);
            _engineChanged?.Invoke();
        }
    }

    private static void ApplyTag(Border tag, TextBlock text, string label, IBrush foreground, string background, string border)
    {
        text.Text = label;
        text.Foreground = foreground;
        tag.Background = Brush.Parse(background);
        tag.BorderBrush = Brush.Parse(border);
    }

    private void DoneButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
