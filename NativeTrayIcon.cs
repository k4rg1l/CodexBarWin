using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CodexBarWin;

public sealed class NativeTrayIcon : IDisposable
{
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int WmUser = 0x0400;
    private const int CallbackMessage = WmUser + 42;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int GwlpWndProc = -4;
    private const int LrLoadFromFile = 0x00000010;
    private const int ImageIcon = 1;
    private const uint TpmReturNCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const int SOk = 0;

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly WndProcDelegate _newWndProc;
    private readonly nint _oldWndProc;
    private readonly IntPtr _iconHandle;
    private bool _disposed;

    public event Action? LeftClicked;
    public event Action? RefreshRequested;
    public event Action? OpenFolderRequested;
    public event Action? OpenLogRequested;
    public event Action? QuitRequested;

    public NativeTrayIcon(IntPtr hwnd, DispatcherQueue dispatcher, string iconPath, string tooltip)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _newWndProc = WndProc;
        _oldWndProc = SetWindowLongPtr(_hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_newWndProc));
        _iconHandle = LoadIconHandle(iconPath);

        var data = CreateData(tooltip);
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.hIcon = _iconHandle;
        Shell_NotifyIcon(NimAdd, ref data);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        var data = CreateData(tooltip);
        data.uFlags = NifTip;
        Shell_NotifyIcon(NimModify, ref data);
    }

    public bool TryGetIconBounds(out TrayIconBounds bounds)
    {
        bounds = default;
        if (_disposed) return false;

        var identifier = new NotifyIconIdentifier
        {
            cbSize = Marshal.SizeOf<NotifyIconIdentifier>(),
            hWnd = _hwnd,
            uID = 1
        };

        var result = Shell_NotifyIconGetRect(ref identifier, out var rect);
        if (result != SOk) return false;

        bounds = new TrayIconBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var data = CreateData("");
        Shell_NotifyIcon(NimDelete, ref data);
        if (_oldWndProc != 0)
        {
            SetWindowLongPtr(_hwnd, GwlpWndProc, _oldWndProc);
        }
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }
    }

    private nint WndProc(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            var mouseMessage = unchecked((int)lParam);
            if (mouseMessage == WmLButtonUp)
            {
                _dispatcher.TryEnqueue(() => LeftClicked?.Invoke());
                return 0;
            }
            if (mouseMessage == WmRButtonUp)
            {
                _dispatcher.TryEnqueue(ShowContextMenu);
                return 0;
            }
        }
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var point)) return;
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, 1, "Refresh Now");
        AppendMenu(menu, MfSeparator, 0, "");
        AppendMenu(menu, MfString, 2, "Open Data Folder");
        AppendMenu(menu, MfString, 3, "Open Log");
        AppendMenu(menu, MfSeparator, 0, "");
        AppendMenu(menu, MfString, 4, "Quit CodexBarWin");
        SetForegroundWindow(_hwnd);
        var command = TrackPopupMenu(menu, TpmReturNCmd | TpmRightButton, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case 1: RefreshRequested?.Invoke(); break;
            case 2: OpenFolderRequested?.Invoke(); break;
            case 3: OpenLogRequested?.Invoke(); break;
            case 4: QuitRequested?.Invoke(); break;
        }
    }

    private NotifyIconData CreateData(string tooltip)
    {
        return new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uCallbackMessage = CallbackMessage,
            szTip = TrimTooltip(tooltip)
        };
    }

    private static string TrimTooltip(string value)
    {
        return value.Length <= 127 ? value : value[..124] + "...";
    }

    private static IntPtr LoadIconHandle(string iconPath)
    {
        if (File.Exists(iconPath))
        {
            var handle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
            if (handle != IntPtr.Zero) return handle;
        }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    public readonly record struct TrayIconBounds(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int CenterX => Left + Width / 2;
        public int CenterY => Top + Height / 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Guid guidItem;
    }

    private delegate nint WndProcDelegate(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, int type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32());
    }

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}


