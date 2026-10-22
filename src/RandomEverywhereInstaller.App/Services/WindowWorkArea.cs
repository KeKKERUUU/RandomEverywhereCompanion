using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RandomEverywhereInstaller.App.Services;

/// <summary>Keep borderless maximization inside the current monitor's physical work area.</summary>
public static class WindowWorkArea
{
    public static Rect RelativeBounds(Rect monitor, Rect work) =>
        new(work.Left - monitor.Left, work.Top - monitor.Top, work.Width, work.Height);

    public static void Attach(Window window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source is null) return;
        source.AddHook(HandleMessage);
        window.Closed += (_, _) => source.RemoveHook(HandleMessage);
    }

    private static nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int getMinMaxInfo = 0x24;
        if (message != getMinMaxInfo) return 0;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return 0;
        var bounds = RelativeBounds(info.Monitor.AsRect(), info.Work.AsRect());
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new PointI { X = (int)bounds.X, Y = (int)bounds.Y };
        limits.MaxSize = new PointI { X = (int)bounds.Width, Y = (int)bounds.Height };
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RectI
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect AsRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public PointI Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
