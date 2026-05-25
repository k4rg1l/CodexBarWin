using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Runtime.InteropServices;
using WinSize = Windows.Graphics.SizeInt32;

namespace CodexBarWin;

public sealed partial class MainWindow : Window
{
    private const int WidthPx = 344;
    private const int CompactHeightPx = 492;
    private const int ExpandedHeightPx = 514;
    private const bool KeepFlyoutOpenWhenUnfocused = false;
    private const int FlyoutGapPx = 10;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExToolWindow = 0x00000080;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int DwmLightBorderColor = unchecked((int)0x00FFF7F3);
    private const int DwmDarkBorderColor = unchecked((int)0x00241115);

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    public StatusViewModel ViewModel { get; }

    private readonly CodexStatusService _statusService;
    private readonly TrayActions _actions;
    private readonly DispatcherTimer _countdownTimer;
    private readonly IntPtr _hwnd;
    private readonly NativeTrayIcon _trayIcon;
    private bool _isVisible;
    private bool _isDarkTheme;
    private int _currentHeightPx = CompactHeightPx;
    private bool _suppressNextTrayClickAfterAutoHide;
    private DateTimeOffset _lastAutoHideAt = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        var paths = AppPathDiscovery.Discover();
        ViewModel = new StatusViewModel();
        Root.DataContext = ViewModel;
        ApplyTheme();

        _statusService = new CodexStatusService(paths, ViewModel);
        _actions = new TrayActions(paths, _statusService, ViewModel);
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.Resize(new WinSize(WidthPx, _currentHeightPx));
        AppWindow.SetIcon("Assets/AppIcon.ico");
        MakeToolWindow();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _trayIcon = new NativeTrayIcon(_hwnd, DispatcherQueue, iconPath, ViewModel.Tooltip);
        _trayIcon.LeftClicked += ToggleFlyout;
        _trayIcon.RefreshRequested += async () => await _actions.RefreshNowAsync();
        _trayIcon.OpenFolderRequested += _actions.OpenDataFolder;
        _trayIcon.OpenLogRequested += _actions.OpenLog;
        _trayIcon.QuitRequested += Quit;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatusViewModel.Tooltip) || e.PropertyName == nameof(StatusViewModel.StateBadge))
            {
                _trayIcon.UpdateTooltip(ViewModel.Tooltip);
            }

            if (e.PropertyName == nameof(StatusViewModel.UsageBuckets) ||
                e.PropertyName == nameof(StatusViewModel.AttentionVisibility))
            {
                UpdateFlyoutSize(reposition: _isVisible);
            }
        };

        _statusService.Start();
        ViewModel.RefreshCountdowns();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _countdownTimer.Tick += (_, _) => ViewModel.RefreshCountdowns();
        _countdownTimer.Start();

        Activated += (_, args) =>
        {
            if (_isVisible && !KeepFlyoutOpenWhenUnfocused && args.WindowActivationState == WindowActivationState.Deactivated)
            {
                HideFlyout(autoHide: true);
            }
        };
    }

    public void HideHostWindow()
    {
        HideFlyout();
    }

    private async void RefreshNow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        await _actions.RefreshNowAsync();
    }

    private void ThemeToggle_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _isDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        ThemeToggleGlyph.Text = _isDarkTheme ? "\u2600" : "\u263E";
        ViewModel.SetTheme(_isDarkTheme);
        ApplyThemeToggleNormalState();
        ApplyRefreshNormalState();
        if (_hwnd != IntPtr.Zero)
        {
            ApplyWindowCornerPreference();
        }
    }

    private void ThemeToggle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ThemeToggleSurface.Background = Brush(_isDarkTheme ? "#44B99BFF" : "#3AA27FFF");
        ThemeToggleSurface.BorderBrush = Brush(_isDarkTheme ? "#FFB99BFF" : "#FFA27FFF");
        ThemeToggleGlyph.Foreground = Brush(_isDarkTheme ? "#FFF5F0FF" : "#FF6D56A8");
    }

    private void ThemeToggle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ApplyThemeToggleNormalState();
    }

    private void ThemeToggle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ThemeToggleSurface.Background = Brush(_isDarkTheme ? "#60B99BFF" : "#54A27FFF");
        ThemeToggleSurface.BorderBrush = Brush(_isDarkTheme ? "#FFE0D2FF" : "#FFA27FFF");
        ThemeToggleGlyph.Foreground = Brush(_isDarkTheme ? "#FFFFFFFF" : "#FF5A4097");
    }

    private void ApplyThemeToggleNormalState()
    {
        ThemeToggleSurface.Background = Brush(_isDarkTheme ? "#1CFFFFFF" : "#24FFFFFF");
        ThemeToggleSurface.BorderBrush = Brush(_isDarkTheme ? "#FFB99BFF" : "#FFA27FFF");
        ThemeToggleGlyph.Foreground = Brush(_isDarkTheme ? "#FFB99BFF" : "#FFA27FFF");
    }

    private void RefreshButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        RefreshButtonChrome.Fill = Brush(_isDarkTheme ? "#44B99BFF" : "#3AA27FFF");
        RefreshButtonChrome.Stroke = Brush(_isDarkTheme ? "#FFB99BFF" : "#FFA27FFF");
        RefreshButtonGlyph.Foreground = Brush(_isDarkTheme ? "#FFF5F0FF" : "#FF6D56A8");
    }

    private void RefreshButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ApplyRefreshNormalState();
    }

    private void ApplyRefreshNormalState()
    {
        RefreshButtonChrome.Fill = Brush(_isDarkTheme ? "#1CFFFFFF" : "#24FFFFFF");
        RefreshButtonChrome.Stroke = Brush(_isDarkTheme ? "#FFB99BFF" : "#FFA27FFF");
        RefreshButtonGlyph.Foreground = Brush(_isDarkTheme ? "#FFB99BFF" : "#FFA27FFF");
    }

    private void RefreshButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RefreshButtonChrome.Fill = Brush(_isDarkTheme ? "#60B99BFF" : "#54A27FFF");
        RefreshButtonChrome.Stroke = Brush(_isDarkTheme ? "#FFE0D2FF" : "#FFA27FFF");
        RefreshButtonGlyph.Foreground = Brush(_isDarkTheme ? "#FFFFFFFF" : "#FF5A4097");
    }

    private void ToggleFlyout()
    {
        if (IsFlyoutVisible())
        {
            _suppressNextTrayClickAfterAutoHide = false;
            HideFlyout();
            return;
        }

        if (_suppressNextTrayClickAfterAutoHide)
        {
            _suppressNextTrayClickAfterAutoHide = false;
            if (DateTimeOffset.UtcNow - _lastAutoHideAt < TimeSpan.FromMilliseconds(650))
            {
                _isVisible = false;
                return;
            }
        }

        if (_isVisible) HideFlyout();
        else ShowFlyout();
    }

    private void ShowFlyout()
    {
        _currentHeightPx = DesiredHeightPx();
        var (x, y) = CalculateFlyoutPosition(_currentHeightPx);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, WidthPx, _currentHeightPx));
        ShowWindow(_hwnd, SwShow);
        ApplyWindowCornerPreference();
        SetWindowPos(_hwnd, HwndTopMost, x, y, WidthPx, _currentHeightPx, SwpShowWindow);
        SetForegroundWindow(_hwnd);
        _isVisible = true;
    }

    private void HideFlyout(bool autoHide = false)
    {
        ShowWindow(_hwnd, SwHide);
        SetWindowPos(_hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        _isVisible = false;
        if (autoHide)
        {
            _suppressNextTrayClickAfterAutoHide = true;
            _lastAutoHideAt = DateTimeOffset.UtcNow;
        }
    }

    private void Quit()
    {
        _statusService.Dispose();
        _trayIcon.Dispose();
        Application.Current.Exit();
    }

    private void MakeToolWindow()
    {
        var style = GetWindowLong(_hwnd, GwlStyle);
        SetWindowLong(_hwnd, GwlStyle, style & ~WsCaption & ~WsThickFrame);
        var exStyle = GetWindowLong(_hwnd, GwlExStyle);
        SetWindowLong(_hwnd, GwlExStyle, (exStyle & ~WsExAppWindow) | WsExToolWindow);
        ApplyWindowCornerPreference();
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void UpdateFlyoutSize(bool reposition)
    {
        var height = DesiredHeightPx();
        if (height == _currentHeightPx && !reposition)
        {
            return;
        }

        _currentHeightPx = height;
        if (reposition)
        {
            var (x, y) = CalculateFlyoutPosition(_currentHeightPx);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, WidthPx, _currentHeightPx));
            SetWindowPos(_hwnd, HwndTopMost, x, y, WidthPx, _currentHeightPx, SwpShowWindow);
        }
        else
        {
            AppWindow.Resize(new WinSize(WidthPx, _currentHeightPx));
        }
    }

    private int DesiredHeightPx()
    {
        return ViewModel.UsageBuckets.Count > 2 || ViewModel.AttentionVisibility == Visibility.Visible
            ? ExpandedHeightPx
            : CompactHeightPx;
    }

    private (int X, int Y) CalculateFlyoutPosition(int heightPx)
    {
        if (_trayIcon.TryGetIconBounds(out var icon))
        {
            var area = NativeScreen.GetWorkAreaNearPoint(icon.CenterX, icon.CenterY);
            var hasRoomLeft = icon.Left - area.Left >= WidthPx + FlyoutGapPx;
            var hasRoomRight = area.Right - icon.Right >= WidthPx + FlyoutGapPx;

            var x = hasRoomLeft || !hasRoomRight
                ? Math.Max(area.Left, icon.Left - WidthPx - FlyoutGapPx)
                : Math.Min(area.Right - WidthPx, icon.Right + FlyoutGapPx);

            var y = icon.Bottom > area.Bottom
                ? area.Bottom - heightPx - FlyoutGapPx
                : Clamp(icon.CenterY - heightPx / 2, area.Top, area.Bottom - heightPx);

            return (x, y);
        }

        var fallback = NativeScreen.GetWorkAreaNearCursor();
        return (
            Clamp(fallback.CursorX - WidthPx + 32, fallback.Left, fallback.Right - WidthPx),
            fallback.Bottom - heightPx - FlyoutGapPx);
    }

    private bool IsFlyoutVisible()
    {
        return IsWindowVisible(_hwnd);
    }

    private void ApplyWindowCornerPreference()
    {
        var preference = DwmWindowCornerPreferenceRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref preference, Marshal.SizeOf<int>());
        var borderColor = _isDarkTheme ? DwmDarkBorderColor : DwmLightBorderColor;
        DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref borderColor, Marshal.SizeOf<int>());
    }

    private static int Clamp(int value, int min, int max)
    {
        if (max < min) return min;
        return Math.Min(Math.Max(value, min), max);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var color = new Windows.UI.Color
        {
            A = Convert.ToByte(hex.Substring(1, 2), 16),
            R = Convert.ToByte(hex.Substring(3, 2), 16),
            G = Convert.ToByte(hex.Substring(5, 2), 16),
            B = Convert.ToByte(hex.Substring(7, 2), 16)
        };
        return new SolidColorBrush(color);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static int GetWindowLong(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? unchecked((int)GetWindowLongPtr64(hWnd, nIndex)) : GetWindowLong32(hWnd, nIndex);
    }

    private static void SetWindowLong(IntPtr hWnd, int nIndex, int value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, value);
        else SetWindowLong32(hWnd, nIndex, value);
    }
}


