using HagoromoChan.Contracts;
using HagoromoChan.Desktop;

internal static class DesktopUiChecks
{
    public static void Placement()
    {
        var leftMonitor = new ScreenRect(-2560, -40, 2560, 1400);
        var rightEdge = new ScreenRect(-180, 1100, 123, 180);
        var left = OverlayLayout.Place(leftMonitor, rightEdge, 380, 400, 8);
        Equal(OverlaySide.Left, left.Side);
        True(left.Bounds.Right <= rightEdge.X - 8, "The left placement does not cover the mascot.");
        Inside(leftMonitor, left.Bounds);

        var rightMonitor = new ScreenRect(0, 0, 1920, 1040);
        var leftEdge = new ScreenRect(0, 400, 123, 180);
        var right = OverlayLayout.Place(rightMonitor, leftEdge, 380, 400, 8);
        Equal(OverlaySide.Right, right.Side);
        True(right.Bounds.X >= leftEdge.Right + 8, "The right placement does not cover the mascot.");
        Inside(rightMonitor, right.Bounds);

        var narrow = new ScreenRect(0, 0, 450, 1000);
        var mascot = new ScreenRect(165, 750, 120, 180);
        var above = OverlayLayout.Place(narrow, mascot, 360, 400, 8);
        Equal(OverlaySide.Above, above.Side);
        True(above.Bounds.Bottom <= mascot.Y - 8, "A vertical fallback fits above.");
        Inside(narrow, above.Bounds);
        Equal(OverlaySide.Below, OverlayLayout.Place(narrow,
            new ScreenRect(165, 100, 120, 180), 360, 400, 8, OverlaySide.Below).Side);

        var restored = OverlayLayout.Clamp(leftMonitor, new ScreenRect(5000, -5000, 123, 180));
        Inside(leftMonitor, restored);
        Equal(leftMonitor.Right - 123, restored.X);
        Equal(leftMonitor.Y, restored.Y);
        var oversized = OverlayLayout.Clamp(narrow, new ScreenRect(-20, -20, 800, 1200));
        Equal(narrow, oversized);

        // Cover the corners and negative coordinates using plausible pixel sizes at different DPIs.
        foreach (var area in new[] { leftMonitor, rightMonitor, narrow, new ScreenRect(1920, -900, 1600, 860) })
        foreach (var width in new[] { 240, 380, 760 })
        foreach (var height in new[] { 120, 420, 880 })
        {
            foreach (var x in new[] { area.X, area.X + area.Width / 2, area.Right - 125 })
            foreach (var y in new[] { area.Y, area.Y + area.Height / 2, area.Bottom - 180 })
            {
                var anchor = OverlayLayout.Clamp(area, new ScreenRect(x, y, 123, 180));
                var placement = OverlayLayout.Place(area, anchor, width, height, 8);
                Inside(area, placement.Bounds);
            }
        }
    }

    public static void Preferences()
    {
        var history = new List<ChatMessage> { User(1, "old-question"), Reply(2, "old-answer") };
        var state = new DesktopPreferences();
        True(state.Observe(history, 2), "A first complete read establishes a baseline.");
        Equal(0, state.UnreadIds.Count);
        Equal(2L, state.ObservedSequence!.Value);
        Equal("old-answer", state.ObservedId);
        True(!state.Observe(history, 2), "Repeated reads do not create notifications.");

        history.Add(User(3, "question-2"));
        history.Add(Reply(4, "reply-2"));
        history.Add(Reply(5, "reply-3"));
        True(state.Observe(history, 5), "Two new answers are observed.");
        True(state.UnreadIds.SetEquals(new[] { "reply-2", "reply-3" }), "Both new answers remain unread.");
        state.UnreadIds.Remove("reply-2");
        state.Height = 260;
        state.Topmost = false;
        state.ScreenX = -250;
        state.ScreenY = 700;

        var directory = Path.Combine(Path.GetTempPath(), "hagoromo-ui-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new DesktopPreferencesStore(directory);
            store.Save(state);
            var reopened = store.Load();
            Equal(260, reopened.Height);
            Equal(false, reopened.Topmost);
            Equal(-250, reopened.ScreenX!.Value);
            Equal(700, reopened.ScreenY!.Value);
            True(reopened.UnreadIds.SetEquals(new[] { "reply-3" }), "Only the unread answer is restored.");
            True(!reopened.Observe(history, 5), "No repeated notification on restart.");
            history.Add(User(6, "question-4"));
            history.Add(Reply(7, "reply-4"));
            True(reopened.Observe(history, 7), "An answer during downtime becomes unread.");
            True(reopened.UnreadIds.SetEquals(new[] { "reply-3", "reply-4" }), "Unread and new answers coexist.");
            store.Save(reopened);
            True(store.Load().UnreadIds.SetEquals(reopened.UnreadIds), "Atomic replacement retains unread identities.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static void CheckpointIdentity()
    {
        var messages = new List<ChatMessage> { User(1, "first"), Reply(2, "first-reply") };
        var state = new DesktopPreferences();
        state.Observe(messages, 2);
        messages.Add(User(3, "accepted-but-not-read"));
        messages.Add(Reply(4, "future-reply"));
        True(!state.Observe(messages, 2), "A POST result cannot advance the complete-read checkpoint.");
        Equal(0, state.UnreadIds.Count);
        True(state.Observe(messages, 4), "A complete read advances the checkpoint.");
        True(state.UnreadIds.SetEquals(new[] { "future-reply" }), "New replies are identified by ID.");
        var replacement = new[] { User(1, "other"), Reply(2, "other-reply"), Reply(4, "replacement") };
        True(state.Observe(replacement, 4), "A replaced database reusing sequences is detected.");
        Equal(0, state.UnreadIds.Count);
        Equal("replacement", state.ObservedId);

        var empty = new DesktopPreferences();
        True(empty.Observe(Array.Empty<ChatMessage>(), 0), "An empty history also establishes a baseline.");
        True(empty.Observe(new[] { User(1, "q"), Reply(2, "r") }, 2), "The first actual reply is unread.");
        True(empty.UnreadIds.SetEquals(new[] { "r" }), "Empty baseline does not discard the first reply.");
    }

    private static ChatMessage User(long sequence, string id) =>
        new(sequence, id, "user", "question", null, DateTimeOffset.UtcNow);
    private static ChatMessage Reply(long sequence, string id) =>
        new(sequence, id, "assistant", "answer", null, DateTimeOffset.UtcNow);

    private static void Inside(ScreenRect area, ScreenRect rect) =>
        True(rect.X >= area.X && rect.Y >= area.Y && rect.Right <= area.Right && rect.Bottom <= area.Bottom,
            $"Panel outside work area: {rect}, area: {area}");

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }
}
