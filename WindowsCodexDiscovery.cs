using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexBarWin;

public sealed record CodexCommand(string Path, string Source);

public sealed record WindowsCodexInstall(
    string CodexHome,
    string AuthJsonPath,
    string ConfigPath,
    string CliLogPath,
    string DesktopLogRoot,
    string VsCodeLogRoot,
    IReadOnlyList<CodexCommand> Commands)
{
    public bool HasAuthJson => File.Exists(AuthJsonPath);
}

public static class WindowsCodexDiscovery
{
    public static WindowsCodexInstall Discover()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var codexHome = Path.Combine(userProfile, ".codex");
        var commands = new List<CodexCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCommand(commands, seen, Path.Combine(appData, "npm", "codex.cmd"), "npm-global");
        AddCommand(commands, seen, Path.Combine(appData, "npm", "codex.exe"), "npm-global");

        foreach (var pathDir in GetPathDirectories())
        {
            AddCommand(commands, seen, Path.Combine(pathDir, "codex.cmd"), "PATH");
            AddCommand(commands, seen, Path.Combine(pathDir, "codex.exe"), "PATH");
        }

        AddNewestRecursive(commands, seen, Path.Combine(localAppData, "OpenAI", "Codex", "bin"), "desktop-local-bin");

        var packagesRoot = Path.Combine(localAppData, "Packages");
        foreach (var packageDir in EnumerateDirectories(packagesRoot, "OpenAI.Codex_*"))
        {
            AddNewestRecursive(
                commands,
                seen,
                Path.Combine(packageDir, "LocalCache", "Local", "OpenAI", "Codex", "bin"),
                "desktop-package-bin",
                maxResults: 4);
        }

        return new WindowsCodexInstall(
            codexHome,
            Path.Combine(codexHome, "auth.json"),
            Path.Combine(codexHome, "config.toml"),
            Path.Combine(codexHome, "log", "codex-tui.log"),
            Path.Combine(localAppData, "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "Codex", "Logs"),
            Path.Combine(appData, "Code", "logs"),
            commands);
    }

    private static IEnumerable<string> GetPathDirectories()
    {
        var value = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var part in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(part));
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(fullPath)) yield return fullPath;
        }
    }

    private static void AddNewestRecursive(
        List<CodexCommand> commands,
        HashSet<string> seen,
        string root,
        string source,
        int maxResults = 8,
        bool packageScoped = false)
    {
        if (!Directory.Exists(root)) return;

        IEnumerable<string> files;
        try
        {
            files = packageScoped
                ? Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                    .Where(path => path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                : Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var file in files
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(maxResults))
        {
            AddCommand(commands, seen, file.FullName, source);
        }
    }

    private static void AddCommand(List<CodexCommand> commands, HashSet<string> seen, string path, string source)
    {
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return;
        }

        if (!File.Exists(path) || !seen.Add(path)) return;
        commands.Add(new CodexCommand(path, source));
    }

    private static IEnumerable<string> EnumerateDirectories(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return directory;
        }
    }
}

