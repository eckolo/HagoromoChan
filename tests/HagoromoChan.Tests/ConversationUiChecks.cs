using System.Globalization;
using HagoromoChan.Desktop;

internal static class ConversationUiChecks
{
    public static void LosslessPages()
    {
        var text = string.Concat(Enumerable.Repeat("日本語\r\nあ\u3099👩‍💻\n", 40))
            + new string('X', 1024) + "\n\n末尾\r\n";
        double Measure(string value)
        {
            var lines = 1;
            var columns = 0;
            var enumerator = StringInfo.GetTextElementEnumerator(value);
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                if (element is "\r\n" or "\n" or "\r")
                {
                    lines++;
                    columns = 0;
                }
                else
                {
                    if (columns == 12) { lines++; columns = 0; }
                    columns++;
                }
            }
            return lines * 20;
        }

        var pages = TextPaging.Split(text, 100, Measure);
        True(pages.Count > 10, "Long text spans multiple complete pages.");
        Equal(text, string.Concat(pages.Select(page => page.Read(text))));
        var boundaries = StringInfo.ParseCombiningCharacters(text).ToHashSet();
        boundaries.Add(text.Length);
        var position = 0;
        foreach (var page in pages)
        {
            Equal(position, page.Start);
            True(boundaries.Contains(page.Start) && boundaries.Contains(page.End),
                "Page boundaries preserve text elements.");
            True(page.Height <= 100 && Measure(page.Read(text)) <= 100, "Every page fits.");
            Equal(page.Start, pages[TextPaging.PageAt(pages, page.Start)].Start);
            position = page.End;
        }
        Equal(text.Length, position);
        var target = pages[3];
        Equal(text[..target.Start] + "差替え😀\r\n" + text[target.End..],
            TextPaging.Replace(text, target, "差替え😀\r\n"));
        var empty = TextPaging.Split("", 100, Measure);
        Equal(1, empty.Count);
        Equal("", empty[0].Read(""));
    }

    public static void StackNavigation()
    {
        int Count(int message) => message == 1 ? 3 : 1;
        double Height(HistoryPosition position) => position.Message == 1 ? 100 : 60;
        var latest = new HistoryPosition(3, 0);
        var first = HistoryNavigation.Fit(latest, 4, Count, Height, 240);
        Equal(1, first.Count);
        Equal(latest, first[0]);
        var older = HistoryNavigation.Older(latest, Count)!.Value;
        Equal(new HistoryPosition(2, 0), older);
        var expanded = HistoryNavigation.Fit(older, 4, Count, Height, 240);
        True(expanded.SequenceEqual(new[] { older, latest }), "An older card is added above the latest.");
        var previousPage = HistoryNavigation.Older(older, Count)!.Value;
        Equal(new HistoryPosition(1, 2), previousPage);
        var atCapacity = HistoryNavigation.Fit(new HistoryPosition(1, 1), 4, Count, Height, 240);
        True(atCapacity.SequenceEqual(new[] { new HistoryPosition(1, 1), new HistoryPosition(1, 2) }),
            "Only complete cards fit; newer stored pages remain reachable.");
        Equal(previousPage, HistoryNavigation.Newer(new HistoryPosition(1, 1), 4, Count)!.Value);
        Equal(older, HistoryNavigation.Newer(previousPage, 4, Count)!.Value);
        Equal(latest, HistoryNavigation.Newer(older, 4, Count)!.Value);
        True(HistoryNavigation.Newer(latest, 4, Count) is null, "Latest is the end.");
        True(HistoryNavigation.Older(new HistoryPosition(0, 0), Count) is null, "Oldest is the start.");
    }

    public static void FixedZoneAndSourceSize()
    {
        var area = new ScreenRect(-2560, -60, 2560, 1400);
        var anchor = new ScreenRect(-205, 1070, 180, 260);
        var zone = OverlayLayout.ConversationZone(area, anchor, 1);
        Equal(OverlaySide.Left, zone.Side);
        True(zone.Bounds.Right < anchor.X, "Horizontal zone does not cover mascot.");
        foreach (var h in new[] { 44, 140, 500, zone.Bounds.Height })
        {
            var panel = new ScreenRect(zone.Bounds.X, zone.Bounds.Bottom - h, zone.Bounds.Width, h);
            Equal(zone.Bounds.Bottom, panel.Bottom);
            Equal(zone.Bounds.X, panel.X);
            True(panel.Y >= area.Y && panel.Bottom <= area.Bottom, "Fixed-bottom surface is on-screen.");
        }
        var narrow = new ScreenRect(0, 0, 450, 1000);
        var portrait = OverlayLayout.ConversationZone(narrow, new ScreenRect(165, 750, 120, 180), 1);
        Equal(OverlaySide.Above, portrait.Side);
        True(portrait.Bounds.Bottom < 750, "Vertical fallback keeps mascot available.");

        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 })
        {
            var size = OverlayLayout.MascotPixels(530, 780, 260, dpi, area);
            True(size.Width <= 530 && size.Height <= 780, "Never enlarge the source in physical pixels.");
            True(size.Height <= 260 * dpi, "Never exceed the requested DIP height.");
            True(size.Width > 0 && size.Height > 0, "Size is positive.");
        }
        Equal(260, OverlayLayout.MascotPixels(530, 780, 260, 1, area).Height);
        Equal(180, OverlayLayout.MascotPixels(123, 180, 260, 1, area).Height);
    }

    public static void PreferencesMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hagoromo-ui-v2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new DesktopPreferencesStore(directory);
            Equal(260, store.Load().Height);
            var file = Path.Combine(directory, "desktop-ui.json");
            File.WriteAllText(file, "{\"height\":180,\"screenX\":-120,\"screenY\":300,\"topmost\":false}");
            var migrated = store.Load();
            Equal(260, migrated.Height);
            Equal(2, migrated.LayoutVersion);
            Equal(-120, migrated.ScreenX!.Value);
            Equal(false, migrated.Topmost);
            migrated.Height = 180;
            store.Save(migrated);
            Equal(180, store.Load().Height);
            File.WriteAllText(file, "{\"height\":120}");
            Equal(120, store.Load().Height);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

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
