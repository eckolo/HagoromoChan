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

    // Fixed work-area zone for the conversation. Its bottom edge and horizontal
    // origin do not depend on message text, page count, or composer height.
    public static OverlayPlacement ConversationZone(ScreenRect area, ScreenRect anchor, double scale)
    {
        int Px(double dip) => Math.Max(1, (int)Math.Floor(dip * scale));
        var margin = Px(10);
        var gap = Px(8);
        var left = anchor.X - area.X - gap - margin;
        var right = area.Right - anchor.Right - gap - margin;
        var above = anchor.Y - area.Y - gap - margin;
        var below = area.Bottom - anchor.Bottom - gap - margin;
        var desired = Px(620);
        var minimum = Px(300);
        if (Math.Max(left, right) >= minimum)
        {
            var useLeft = left >= right;
            var width = Math.Min(desired, useLeft ? left : right);
            return new OverlayPlacement(Clamp(area, new ScreenRect(
                useLeft ? anchor.X - gap - width : anchor.Right + gap,
                area.Y + margin, width, Math.Max(1, area.Height - 2 * margin))),
                useLeft ? OverlaySide.Left : OverlaySide.Right);
        }
        if (Math.Max(above, below) >= Px(240))
        {
            var useAbove = above >= below;
            var width = Math.Min(desired, Math.Max(1, area.Width - 2 * margin));
            var height = useAbove ? above : below;
            return new OverlayPlacement(Clamp(area, new ScreenRect(
                anchor.X + (anchor.Width - width) / 2,
                useAbove ? anchor.Y - gap - height : anchor.Bottom + gap, width, height)),
                useAbove ? OverlaySide.Above : OverlaySide.Below);
        }

        // Extremely small work areas cannot hold all surfaces without overlap. Keep
        // the controls on-screen; the fixed navigation still allows dismissal.
        var fallbackWidth = Math.Min(desired, Math.Max(1, area.Width - 2 * margin));
        return new OverlayPlacement(Clamp(area, new ScreenRect(area.X + margin, area.Y + margin,
            fallbackWidth, Math.Max(1, area.Height - 2 * margin))),
            left >= right ? OverlaySide.Left : OverlaySide.Right);
    }

    // Keep the status frame in the mascot's own width, including after resize
    // or monitor changes. No discontinuous corridor-based width threshold.
    public static int StatusWidth(ScreenRect area, ScreenRect mascot) =>
        Math.Clamp(mascot.Width, 1, Math.Max(1, area.Width));

    // Accept only candidates adjacent to the mascot or its adjacent auxiliary slot.
    // Clamp on the cross axis, not along the distance from the chosen anchor.
    // No fallback to remote corners of the monitor.
    public static ScreenRect? NearbyAuxiliary(ScreenRect area, ScreenRect mascot,
        int width, int height, int gap, OverlaySide preferred, IReadOnlyList<ScreenRect> obstacles,
        ScreenRect? secondary = null)
    {
        if (width < 1 || height < 1 || width > area.Width || height > area.Height) return null;
        gap = Math.Max(0, gap);
        var sides = new[] { preferred, OverlaySide.Above, OverlaySide.Below,
            OverlaySide.Left, OverlaySide.Right }.Distinct().ToArray();
        var anchors = secondary is { } near ? new[] { mascot, near } : new[] { mascot };
        foreach (var anchor in anchors)
        foreach (var side in sides)
        {
            var vertical = side is OverlaySide.Above or OverlaySide.Below;
            var cross = vertical
                ? new[] { anchor.X + (anchor.Width - width) / 2, anchor.X, anchor.Right - width }
                : new[] { anchor.Y + (anchor.Height - height) / 2, anchor.Y, anchor.Bottom - height };
            foreach (var position in cross.Distinct())
            {
                var x = vertical ? Math.Clamp(position, area.X, area.Right - width)
                    : side == OverlaySide.Left ? anchor.X - gap - width : anchor.Right + gap;
                var y = !vertical ? Math.Clamp(position, area.Y, area.Bottom - height)
                    : side == OverlaySide.Above ? anchor.Y - gap - height : anchor.Bottom + gap;
                var candidate = new ScreenRect(x, y, width, height);
                if (candidate.X < area.X || candidate.Y < area.Y ||
                    candidate.Right > area.Right || candidate.Bottom > area.Bottom ||
                    Intersects(candidate, mascot) || obstacles.Any(item => Intersects(candidate, item)))
                    continue;
                return candidate;
            }
        }
        return null;
    }

    // Prefer the free corridor below the mascot, then immediately above or to
    // its side. Do not reserve an invisible hover menu or a remote screen corner.
    public static ScreenRect? StatusPlacement(ScreenRect area, ScreenRect mascot,
        OverlayPlacement conversation, int width, int height, int gap, bool conversationVisible) =>
        NearbyAuxiliary(area, mascot, width, height, gap, OverlaySide.Below, [conversation.Bounds])
        ?? (conversationVisible ? null : NearbyAuxiliary(area, mascot, width, height, gap, OverlaySide.Below, []));

    public static bool Intersects(ScreenRect a, ScreenRect b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    public static (int Width, int Height) MascotPixels(int imageWidth, int imageHeight,
        int requestedHeightDip, double scale, ScreenRect area)
    {
        var ratio = (double)imageWidth / imageHeight;
        var margin = (int)Math.Ceiling(16 * scale);
        var height = (int)Math.Floor(Math.Min(Math.Min(imageHeight, requestedHeightDip * scale),
            Math.Min(Math.Max(1, area.Height - margin), Math.Max(1, area.Width - margin) / ratio)));
        height = Math.Clamp(height, 1, imageHeight);
        var width = Math.Clamp((int)Math.Floor(height * ratio), 1, imageWidth);
        return (width, height);
    }
}
