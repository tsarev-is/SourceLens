using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SourceLens.Integrations.Cli;

public sealed record CliProbeResult(bool Found, string Version, string ResolvedPath)
{
    /// <summary>
    /// Модели, которые позволяет выбрать CLI (см. <see cref="CliModelCatalog"/>); пусто — выяснить не удалось.
    /// </summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Проверка доступности CLI-движка для Settings-окна: резолв бинарника
/// (абсолютный путь или поиск по PATH) и запуск <c>--version</c> с коротким таймаутом.
/// </summary>
public static class CliEngineProbe
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static async Task<CliProbeResult> Probe(string binaryPath, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
            return new CliProbeResult(false, string.Empty, string.Empty);

        var resolved = ResolveExisting(binaryPath);
        if (resolved == null)
        {
            Logger.Info("CLI probe: '{0}' not found", binaryPath);
            return new CliProbeResult(false, string.Empty, binaryPath);
        }

        var version = await TryGetVersion(resolved, timeout ?? TimeSpan.FromSeconds(10));
        Logger.Info("CLI probe: '{0}' resolved to '{1}', version '{2}'", binaryPath, resolved, version);
        return new CliProbeResult(true, version, resolved);
    }

    private static string? ResolveExisting(string binaryPath)
    {
        if (Path.IsPathRooted(binaryPath))
            return File.Exists(binaryPath) ? binaryPath : null;

        if (binaryPath.Contains(Path.DirectorySeparatorChar) || binaryPath.Contains(Path.AltDirectorySeparatorChar))
        {
            var full = Path.GetFullPath(binaryPath);
            return File.Exists(full) ? full : null;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var extensions = isWindows && !Path.HasExtension(binaryPath)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, binaryPath + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static async Task<string> TryGetVersion(string resolvedPath, TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = resolvedPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");

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
                Logger.Warn("CLI probe: '{0} --version' timed out after {1}", resolvedPath, timeout);
                return string.Empty;
            }

            var stdout = await stdoutTask;
            if (process.ExitCode != 0)
                return string.Empty;

            var firstLine = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return firstLine ?? string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "CLI probe: failed to run '{0} --version'", resolvedPath);
            return string.Empty;
        }
    }
}
