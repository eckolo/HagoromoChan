using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HagoromoChan.Desktop;

internal static class NativeDesktop
{
    private const uint NearestMonitor = 2;
    private const uint PrimaryMonitor = 1;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const int MouseActivate = 0x0021;

    public static nint Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    public static void PreventMouseActivation(Window window)
    {
        if (HwndSource.FromHwnd(Handle(window)) is HwndSource source)
            source.AddHook(NoActivateHook);
    }

    private static nint NoActivateHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != MouseActivate)
            return 0;
        handled = true;
        return 3; // MA_NOACTIVATE: deliver the mouse event, but do not activate.
    }

    public static ScreenRect Bounds(Window window)
    {
        if (!GetWindowRect(Handle(window), out var rect))
            throw new InvalidOperationException("ウィンドウ位置を取得できません。");
        return rect.ToScreenRect();
    }

    public static (int X, int Y) Cursor()
    {
        if (!GetCursorPos(out var point))
            throw new InvalidOperationException("マウス位置を取得できません。");
        return (point.X, point.Y);
    }

    public static ScreenRect WorkArea(Window window) =>
        WorkArea(MonitorFromWindow(Handle(window), NearestMonitor));

    public static ScreenRect WorkArea(int x, int y) =>
        WorkArea(MonitorFromPoint(new NativePoint { X = x, Y = y }, NearestMonitor));

    public static ScreenRect PrimaryWorkArea() =>
        WorkArea(MonitorFromPoint(new NativePoint(), PrimaryMonitor));

    private static ScreenRect WorkArea(nint monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("モニターの作業領域を取得できません。");
        return info.Work.ToScreenRect();
    }

    public static double Scale(Window window)
    {
        var dpi = GetDpiForWindow(Handle(window));
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    public static void Move(Window window, int x, int y)
    {
        var handle = Handle(window);
        if (GetWindowRect(handle, out var current) && current.Left == x && current.Top == y)
            return;
        if (!SetWindowPos(handle, 0, x, y, 0, 0, NoSize | NoZOrder | NoActivate))
            throw new InvalidOperationException("ウィンドウ位置を変更できません。");
    }

    // Populated by the native Win32 calls below.
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly ScreenRect ToScreenRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

#pragma warning restore CS0649

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
