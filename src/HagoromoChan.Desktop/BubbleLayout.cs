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
        // Retain the original minimum clearance, even when the nominal sum is smaller.
        Math.Max(Vertical(side) ? 74 : 62,
            2 * (RoundUp(FrameBorder, scale) + RoundUp(FramePadding, scale))
            + RoundUp(HistoryHeader, scale) + RoundUp(HistoryHeaderGap, scale)
            + (Vertical(side) ? RoundUp(TailDepth, scale) : 0) + RoundUp(gap, scale));

    public static double HistoryBodyBudget(double available, double chrome, double scale) =>
        Math.Max(0, RoundDown(available - chrome, scale));

    // Physical HWND/client pixels are capped before converting to DIP. Mixing
    // owner and destination DPI here can give WPF more space than it can paint.
    public static double ClientDip(int requestedPixels, int clientPixels, double scale) =>
        Math.Max(0, Math.Min(requestedPixels, clientPixels)) / scale;

    public static double ConversationBodyBudget(double clientHeight, double navigationHeight, double scale) =>
        Math.Max(0, RoundDown(clientHeight - RoundUp(navigationHeight, scale) - RoundUp(2, scale), scale));

    public static double ComposerBodyBudget(double available, double chrome, double feedback,
        double minimumBody, double historyReserve, double scale)
    {
        var body = HistoryBodyBudget(available - feedback, chrome, scale);
        var reserved = HistoryBodyBudget(available - feedback - historyReserve, chrome, scale);
        // Keep one readable history page when possible, not a fixed percentage
        // of the surface. If both cannot fit, retain the editable draft.
        return reserved >= minimumBody ? reserved : body;
    }

    // The caller supplies a conservative height from the wrapping text probes.
    // Round up and leave one device pixel for the last baseline / layout rounding.
    public static double HistoryBodyHeight(double desired, double scale) => RoundUp(desired, scale) + 1 / scale;
}
