using System.Runtime.InteropServices;

namespace SourceLens.Integrations.Cli;

internal static class CliBinaryResolver
{
    public static string Resolve(string binaryPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return binaryPath;

        if (Path.IsPathRooted(binaryPath))
            return binaryPath;
        if (binaryPath.Contains(Path.DirectorySeparatorChar) || binaryPath.Contains(Path.AltDirectorySeparatorChar))
            return binaryPath;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return binaryPath;

        var hasExtension = Path.HasExtension(binaryPath);
        var extensions = hasExtension
            ? new[] { string.Empty }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, binaryPath + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return binaryPath;
    }
}
