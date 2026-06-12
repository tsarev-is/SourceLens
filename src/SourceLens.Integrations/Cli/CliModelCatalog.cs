using System.Diagnostics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SourceLens.Domain;

namespace SourceLens.Integrations.Cli;

/// <summary>
/// Список моделей, которые реально позволяет выбрать установленный CLI-агент:
/// Claude — алиасы из help-текста опции <c>--model</c>; Codex — каталог <c>codex debug models</c> (JSON).
/// Пустой результат означает «выяснить не удалось» — вызывающая сторона остаётся на fallback-списке из конфигурации.
/// </summary>
public static class CliModelCatalog
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static async Task<string[]> List(string provider, string resolvedPath, TimeSpan? timeout = null)
    {
        try
        {
            var models = provider switch
            {
                EngineSettings.ClaudeProvider => ParseClaudeHelp(
                    await RunForStdout(resolvedPath, new[] { "--help" }, timeout ?? DefaultTimeout)),
                EngineSettings.CodexProvider => ParseCodexCatalog(
                    await RunForStdout(resolvedPath, new[] { "debug", "models" }, timeout ?? DefaultTimeout)),
                _ => Array.Empty<string>(),
            };
            Logger.Info("Model catalog for {0} ('{1}'): [{2}]", provider, resolvedPath, string.Join(", ", models));
            return models;
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "Model catalog discovery failed for {0} ('{1}')", provider, resolvedPath);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Достаёт алиасы моделей из блока опции <c>--model</c> вывода <c>claude --help</c>:
    /// «Provide an alias for the latest model (e.g. 'fable', 'opus', or 'sonnet') …».
    /// Полные имена вида 'claude-fable-5' (с дефисами) алиасами не считаются.
    /// </summary>
    public static string[] ParseClaudeHelp(string helpText)
    {
        var lines = helpText.Split('\n');
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^\s*--model\s"));
        if (start < 0)
            return Array.Empty<string>();

        var block = lines[start];
        for (var i = start + 1; i < lines.Length && !Regex.IsMatch(lines[i], @"^\s*-{1,2}\w"); i++)
            block += "\n" + lines[i];

        return Regex.Matches(block, @"'([a-z][a-z0-9]*)'")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Парсит JSON-каталог <c>codex debug models</c>: берёт слаги моделей с <c>visibility == "list"</c>,
    /// упорядоченные по <c>priority</c> (меньше — выше в списке выбора Codex CLI).
    /// </summary>
    public static string[] ParseCodexCatalog(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        var models = JObject.Parse(json)["models"] as JArray;
        if (models == null)
            return Array.Empty<string>();

        return models
            .OfType<JObject>()
            .Where(m => (string?)m["visibility"] == "list")
            .OrderBy(m => (int?)m["priority"] ?? int.MaxValue)
            .Select(m => (string?)m["slug"])
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Select(slug => slug!)
            .Distinct()
            .ToArray();
    }

    private static async Task<string> RunForStdout(string resolvedPath, string[] arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Logger.Warn("Model catalog: '{0} {1}' timed out after {2}", resolvedPath, string.Join(' ', arguments), timeout);
            return string.Empty;
        }

        var stdout = await stdoutTask;
        return process.ExitCode == 0 ? stdout : string.Empty;
    }
}
