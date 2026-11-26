namespace HagoromoChan.Desktop;

// The cursor and mascot bounds share physical screen coordinates. Distances are
// measured from the mascot's edge in DIPs so nearby gaze also works on scaled monitors.
internal sealed class CursorGaze
{
    private const double EnterDistanceDip = 96;
    private const double LeaveDistanceDip = 120;
    private const int DwellMs = 300;
    private const int PanelHoldMs = 1000;

    private long? _nearSince;
    private bool _trackingNear;
    private long? _lastPanelInput;
    private (double X, double Y)? _lastDirection;

    public void NotePanelInteraction(long nowMs) => _lastPanelInput = nowMs;

    public (double X, double Y)? Sample(ScreenRect mascotBounds, double dpiScale,
        double cursorX, double cursorY, long nowMs)
    {
        if (mascotBounds.Width <= 0 || mascotBounds.Height <= 0 || !double.IsFinite(dpiScale)
            || dpiScale <= 0 || !double.IsFinite(cursorX) || !double.IsFinite(cursorY))
        {
            Reset();
            return null;
        }

        var left = (double)mascotBounds.X;
        var top = (double)mascotBounds.Y;
        var outsideX = cursorX - Math.Clamp(cursorX, left, left + mascotBounds.Width);
        var outsideY = cursorY - Math.Clamp(cursorY, top, top + mascotBounds.Height);
        var distanceDip = double.Hypot(outsideX, outsideY) / dpiScale;
        if (distanceDip > (_trackingNear ? LeaveDistanceDip : EnterDistanceDip))
        {
            _nearSince = null;
            _trackingNear = false;
        }
        else
        {
            _nearSince ??= nowMs;
            _trackingNear = _trackingNear || nowMs - _nearSince.Value >= DwellMs;
        }

        var recentPanelInput = _lastPanelInput is long last && nowMs - last < PanelHoldMs;
        if (!_trackingNear && !recentPanelInput)
        {
            _lastDirection = null;
            return null;
        }

        var dx = cursorX - (left + mascotBounds.Width * .5);
        var dy = cursorY - (top + mascotBounds.Height * .25);
        var length = double.Hypot(dx, dy);
        // At eye level a sub-DIP move can point in the opposite direction.
        if (length > dpiScale)
            _lastDirection = (dx / length, dy / length);
        return _lastDirection;
    }

    public void Reset()
    {
        _nearSince = null;
        _trackingNear = false;
        _lastPanelInput = null;
        _lastDirection = null;
    }
}
