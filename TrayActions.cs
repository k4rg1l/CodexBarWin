using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CodexBarWin;

public sealed class TrayActions
{
    private readonly AppPaths _paths;
    private readonly CodexStatusService _statusService;
    private readonly StatusViewModel _viewModel;

    public TrayActions(AppPaths paths, CodexStatusService statusService, StatusViewModel viewModel)
    {
        _paths = paths;
        _statusService = statusService;
        _viewModel = viewModel;
    }

    public async Task RefreshNowAsync()
    {
        await _statusService.RefreshNowAsync(manual: true);
    }

    public void OpenDataFolder() => Open(_paths.DataRoot);

    public void OpenLog()
    {
        if (File.Exists(_paths.LogPath)) Open(_paths.LogPath);
        else
        {
            _viewModel.SetFallback("No CodexBarWin log has been written yet.");
            OpenDataFolder();
        }
    }

    private static void Open(string target)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }
}

