using Avalonia;
using Avalonia.Headless;
using SourceLens.Tests.Ui;
using SourceLens.Windows;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SourceLens.Tests.Ui;

/// <summary>
/// Headless-приложение для UI-тестов: реальный App (FluentTheme) без дисплея.
/// Skia-рендеринг включён, чтобы тесты могли снимать реальные кадры окна
/// (CaptureRenderedFrame) и ловить баги перерисовки, а не только состояния.
/// </summary>
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
