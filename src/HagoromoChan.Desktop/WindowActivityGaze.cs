using System.Runtime.InteropServices;

namespace HagoromoChan.Desktop;

// Called by the waiting animation on the UI thread. No hooks or independent timers.
internal sealed class WindowActivityGaze
{
    private const int SampleIntervalMs = 300;
    private const int HoldMs = 1500;
    private const int ExtendedStyle = -20;
    private const int ToolWindow = 0x00000080;
    private const int NoActivate = 0x08000000;
    private const int Cloaked = 14;

    private Dictionary<nint, ScreenRect> _previous = new();
    private Dictionary<nint, ScreenRect> _current = new();
    private nint _previousForeground;
    private nint _target;
    private bool _hasBaseline;
    private long _nextSampleAt;
    private long _holdUntil;

    public (double X, double Y)? Sample(ScreenRect mascotBounds)
    {
        var now = Environment.TickCount64;
        if (now >= _nextSampleAt)
        {
            _nextSampleAt = now + SampleIntervalMs;
            ObserveWindows(now);
        }

        if (now >= _holdUntil || !_previous.TryGetValue(_target, out var target))
            return null;

        // Both points are in physical screen pixels, including on monitors left of the primary.
        var dx = (target.X + target.Width * .5) - (mascotBounds.X + mascotBounds.Width * .5);
        var dy = (target.Y + target.Height * .5) - (mascotBounds.Y + mascotBounds.Height * .25);
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < 1 ? null : (dx / length, dy / length);
    }

    public void Reset()
    {
        _current.Clear();
        _previous.Clear();
        _previousForeground = 0;
        _target = 0;
        _hasBaseline = false;
        _nextSampleAt = 0;
        _holdUntil = 0;
    }

    private void ObserveWindows(long now)
    {
        _current.Clear();
        var shell = GetShellWindow();
        var ownProcess = (uint)Environment.ProcessId;
        if (!EnumWindows((window, _) =>
        {
            if (window != shell && TryGetVisibleBounds(window, ownProcess, out var bounds))
                _current[window] = bounds;
            return true;
        }, 0))
        {
            // Don't turn an incomplete scan into a burst of supposedly new windows.
            _hasBaseline = false;
            _target = 0;
            return;
        }

        var foreground = GetForegroundWindow();
        if (_hasBaseline)
        {
            nint moved = 0;
            nint appeared = 0;
            foreach (var (window, bounds) in _current)
            {
                if (_previous.TryGetValue(window, out var previousBounds))
                {
                    if (bounds != previousBounds && (moved == 0 || window == foreground))
                        moved = window;
                }
                else if (appeared == 0)
                {
                    appeared = window;
                }
            }

            // Actual motion wins, including a window moving in the background.
            var candidate = moved;
            if (candidate == 0 && foreground != _previousForeground && _current.ContainsKey(foreground))
                candidate = foreground;
            if (candidate == 0)
                candidate = appeared;
            if (candidate != 0)
            {
                _target = candidate;
                _holdUntil = now + HoldMs;
            }
            else if (!_current.ContainsKey(_target))
            {
                _target = 0;
            }
        }

        (_previous, _current) = (_current, _previous);
        _previousForeground = foreground;
        _hasBaseline = true;
    }

    private static bool TryGetVisibleBounds(nint window, uint ownProcess, out ScreenRect bounds)
    {
        bounds = default;
        if (!IsWindowVisible(window) || IsIconic(window)
            || GetWindowThreadProcessId(window, out var process) == 0 || process == ownProcess
            || (GetWindowLongW(window, ExtendedStyle) & (ToolWindow | NoActivate)) != 0
            || !GetWindowRect(window, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top
            || MonitorFromRect(in rect, 0) == 0)
            return false;
        if (DwmGetWindowAttribute(window, Cloaked, out var cloaked, sizeof(int)) != 0 || cloaked != 0)
            return false;

        bounds = new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return true;
    }

#pragma warning disable CS0649 // Populated by GetWindowRect.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }
#pragma warning restore CS0649

    private delegate bool EnumWindowProc(nint window, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(in NativeRect rect, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
