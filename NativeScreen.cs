using System;
using System.Runtime.InteropServices;

namespace CodexBarWin;

public static class NativeScreen
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static WorkArea GetWorkAreaNearCursor()
    {
        if (!GetCursorPos(out var point))
        {
            return new WorkArea(0, 0, 1920, 1080, 960, 540);
        }

        return GetWorkAreaNearPoint(point.X, point.Y);
    }

    public static WorkArea GetWorkAreaNearPoint(int x, int y)
    {
        var point = new Point { X = x, Y = y };
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new WorkArea(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right,
                info.rcWork.Bottom,
                x,
                y);
        }

        return new WorkArea(0, 0, 1920, 1080, x, y);
    }

    public sealed record WorkArea(int Left, int Top, int Right, int Bottom, int CursorX, int CursorY);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}


