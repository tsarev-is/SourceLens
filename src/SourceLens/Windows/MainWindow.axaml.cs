using Avalonia.Controls;

namespace SourceLens.Windows;

/// <summary>
/// Окно ошибок конфигурации: показывается вместо RagWindow, если composition root
/// не смог прочитать/провалидировать appsettings.json или собрать зависимости.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(string errorMessage)
    {
        InitializeComponent();
        ErrorTextBox.Text = errorMessage;
    }
}
