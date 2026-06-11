using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceLens.Windows;

public partial class MainWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public MainWindow()
    {
        InitializeComponent();
    }

    private void ButtonRequest_OnClick(object? sender, RoutedEventArgs e)
    {
        Logger.Info("Request button clicked");
        AnswerTextBox.Text = "Заглушка: здесь будет ответ на запрос.";
    }
}
