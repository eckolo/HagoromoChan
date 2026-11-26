namespace HagoromoChan.Desktop;

internal static class BubbleLayout
{
    public const double TailDepth = 12;
    public const double FrameBorder = 1;
    public const double FramePadding = 9;
    public const double HistoryHeader = 27;
    public const double HistoryHeaderGap = 3;
    public const double MinimumBodyWidth = 36;

    public static bool Vertical(OverlaySide side) => side is OverlaySide.Above or OverlaySide.Below;
    public static double RoundUp(double dip, double scale) => Math.Ceiling(dip * scale) / scale;
    public static double RoundDown(double dip, double scale) => Math.Floor(dip * scale) / scale;

    // The frame's horizontal origin must not depend on which gutter holds the tail.
    public static double BodyWidth(double width, double scale) => Math.Max(0, RoundDown(width
        - 2 * (RoundUp(TailDepth, scale) + RoundUp(FrameBorder, scale) + RoundUp(FramePadding, scale)), scale));

    public static double HistoryChrome(OverlaySide side, double gap, double scale) =>
        2 * (RoundUp(FrameBorder, scale) + RoundUp(FramePadding, scale))
        + RoundUp(HistoryHeader, scale) + RoundUp(HistoryHeaderGap, scale)
        + (Vertical(side) ? RoundUp(TailDepth, scale) : 0) + RoundUp(gap, scale);

    public static double HistoryBodyBudget(double available, double chrome, double scale) =>
        Math.Max(0, RoundDown(available - chrome, scale));

    // Both the probe and renderer use the same read-only text control. Round up,
    // and leave one device pixel for its last baseline / layout rounding.
    public static double HistoryBodyHeight(double desired, double scale) => RoundUp(desired, scale) + 1 / scale;
}
