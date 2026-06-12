using System.Diagnostics;
using System.Text;

namespace SourceLens.Integrations.Cli;

public sealed class CliPromptRunner
{
    private const int MaxArgumentPromptLength = 8000;

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly string _binaryPath;
    private readonly string? _workingDirectory;
    private readonly TimeSpan _timeout;
    private readonly string _displayName;

    public CliPromptRunner(string binaryPath, string? workingDirectory, TimeSpan timeout, string displayName)
    {
        _binaryPath = binaryPath;
        _workingDirectory = workingDirectory;
        _timeout = timeout;
        _displayName = displayName;
    }

    public async Task<string> RunAsync(Action<ProcessStartInfo, bool> configureArguments, string stdinPayload)
    {
        var useStdin = stdinPayload.Length > MaxArgumentPromptLength;

        var startInfo = new ProcessStartInfo
        {
            FileName = _binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = useStdin,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory ?? Environment.CurrentDirectory,
        };
        if (useStdin)
            startInfo.StandardInputEncoding = Encoding.UTF8;
        configureArguments(startInfo, useStdin);

        Logger.Info("Starting {0} ({1}): prompt {2} chars, via {3}", _displayName, _binaryPath, stdinPayload.Length, useStdin ? "stdin" : "argument");
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        if (useStdin)
        {
            await process.StandardInput.WriteAsync(stdinPayload);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Logger.Warn("{0} call timed out after {1}", _displayName, _timeout);
            throw new TimeoutException($"{_displayName} call timed out after {_timeout}");
        }

        var stdout = await stdoutTask;

        Logger.Info("{0} finished in {1:F1}s with exit code {2}", _displayName, stopwatch.Elapsed.TotalSeconds, process.ExitCode);

        if (process.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            throw new InvalidOperationException($"{_displayName} exited with code {process.ExitCode}: {err}");
        }

        return stdout.Trim();
    }
}
