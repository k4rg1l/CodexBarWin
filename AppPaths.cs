using System;
using System.IO;

namespace CodexBarWin;

public sealed record AppPaths(
    string DataRoot,
    string StatusJson,
    string HistoryJsonl,
    string LogPath);

public static class AppPathDiscovery
{
    public static AppPaths Discover()
    {
        var envRoot = Environment.GetEnvironmentVariable("CODEXBARWIN_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(envRoot))
        {
            envRoot = Environment.GetEnvironmentVariable("CODEXBAR_DATA_ROOT");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = !string.IsNullOrWhiteSpace(envRoot)
            ? envRoot
            : Path.Combine(localAppData, "CodexBarWin");

        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        TryMigrateOldCache(Path.Combine(localAppData, "CodexBar"), root);

        return new AppPaths(
            root,
            Path.Combine(root, "status.json"),
            Path.Combine(root, "history.jsonl"),
            Path.Combine(root, "CodexBarWin.log"));
    }

    private static void TryMigrateOldCache(string oldRoot, string newRoot)
    {
        if (!Directory.Exists(oldRoot) || oldRoot.Equals(newRoot, StringComparison.OrdinalIgnoreCase)) return;

        foreach (var fileName in new[] { "status.json", "history.jsonl" })
        {
            var oldPath = Path.Combine(oldRoot, fileName);
            var newPath = Path.Combine(newRoot, fileName);
            if (!File.Exists(oldPath) || File.Exists(newPath)) continue;

            try
            {
                File.Copy(oldPath, newPath);
            }
            catch
            {
            }
        }
    }
}

