using Avalonia;
using Avalonia.Headless;
using SourceLens.Tests.Ui;
using SourceLens.Windows;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SourceLens.Tests.Ui;

/// <summary>
/// Headless-приложение для UI-тестов: реальный App (FluentTheme) без дисплея.
/// </summary>
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
