namespace HagoromoChan.Desktop;

// All inputs and outputs are physical screen pixels, including negative monitor coordinates.
internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

internal enum OverlaySide { Right, Left, Above, Below }

internal readonly record struct OverlayPlacement(ScreenRect Bounds, OverlaySide Side);

internal static class OverlayLayout
{
    public static ScreenRect Clamp(ScreenRect area, ScreenRect rect)
    {
        var width = Math.Clamp(rect.Width, 1, Math.Max(1, area.Width));
        var height = Math.Clamp(rect.Height, 1, Math.Max(1, area.Height));
        return new ScreenRect(
            Math.Clamp(rect.X, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(rect.Y, area.Y, Math.Max(area.Y, area.Bottom - height)),
            width, height);
    }

    public static OverlayPlacement Place(ScreenRect area, ScreenRect anchor, int width, int height,
        int gap, OverlaySide? prefer = null)
    {
        width = Math.Clamp(width, 1, Math.Max(1, area.Width));
        height = Math.Clamp(height, 1, Math.Max(1, area.Height));
        gap = Math.Max(0, gap);
        var choices = new (OverlaySide Side, int Space, bool Fits)[]
        {
            (OverlaySide.Right, area.Right - anchor.Right - gap, area.Right - anchor.Right - gap >= width),
            (OverlaySide.Left, anchor.X - area.X - gap, anchor.X - area.X - gap >= width),
            (OverlaySide.Above, anchor.Y - area.Y - gap, anchor.Y - area.Y - gap >= height),
            (OverlaySide.Below, area.Bottom - anchor.Bottom - gap, area.Bottom - anchor.Bottom - gap >= height)
        };

        // Prefer a requested direction only if it fits. Otherwise prefer a horizontal side,
        // then a vertical side. If none fits, use the side with the largest relative free space.
        var fitting = choices.Where(choice => choice.Fits).ToArray();
        OverlaySide side;
        if (prefer is { } preferred && fitting.Any(choice => choice.Side == preferred))
            side = preferred;
        else if (fitting.Length != 0)
            side = fitting.OrderBy(choice => choice.Side is OverlaySide.Right or OverlaySide.Left ? 0 : 1)
                .ThenByDescending(choice => choice.Space).First().Side;
        else
            side = choices.OrderByDescending(choice => (double)choice.Space /
                (choice.Side is OverlaySide.Right or OverlaySide.Left ? width : height)).First().Side;

        var rect = side switch
        {
            OverlaySide.Right => new ScreenRect(anchor.Right + gap, anchor.Y, width, height),
            OverlaySide.Left => new ScreenRect(anchor.X - gap - width, anchor.Y, width, height),
            OverlaySide.Above => new ScreenRect(anchor.X + (anchor.Width - width) / 2,
                anchor.Y - gap - height, width, height),
            _ => new ScreenRect(anchor.X + (anchor.Width - width) / 2, anchor.Bottom + gap, width, height)
        };
        return new OverlayPlacement(Clamp(area, rect), side);
    }
}
