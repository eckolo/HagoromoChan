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

    public static void BubbleBodyAndFinalLine()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var width = BubbleLayout.RoundDown(301, scale);
            var inset = BubbleLayout.RoundUp(BubbleLayout.TailDepth, scale);
            var body = BubbleLayout.BodyWidth(width, scale);
            True(body > 0 && body <= width - 2 * inset - 20 + 1e-8,
                "The body fits after reserving both tail gutters and frame insets.");
            var horizontalChrome = BubbleLayout.HistoryChrome(OverlaySide.Left, 6, scale);
            Equal(horizontalChrome, BubbleLayout.HistoryChrome(OverlaySide.Right, 6, scale));
            Equal(BubbleLayout.HistoryChrome(OverlaySide.Above, 6, scale),
                BubbleLayout.HistoryChrome(OverlaySide.Below, 6, scale));
            True(BubbleLayout.BodyWidth(30, scale) == 0, "No forced text width outside a tiny work area.");

            foreach (var side in Enum.GetValues<OverlaySide>())
            {
                var chrome = BubbleLayout.HistoryChrome(side, 6, scale);
                var lineHeight = 21.3;
                double Measure(string s) => BubbleLayout.HistoryBodyHeight(
                    (1 + s.Count(c => c == '\n')) * lineHeight, scale);
                var exact = "一行目\r\n二行目\n三行目";
                var available = Measure(exact) + chrome + 1e-8;
                var budget = BubbleLayout.HistoryBodyBudget(available, chrome, scale);
                True(Math.Abs(budget - Measure(exact)) < 1e-8, "Exact fit must use the full body.");
                Equal(1, TextPaging.Split(exact, budget + 1e-8, Measure).Count);
                var longText = exact + "\r\n最終行";
                var pages = TextPaging.Split(longText, budget + 1e-8, Measure);
                Equal(2, pages.Count);
                True(pages.All(p => p.Height + chrome <= available), "No page may exceed the frame height.");
                Equal(longText, string.Concat(pages.Select(p => p.Read(longText))));
                Equal(new HistoryPosition(0, 1), HistoryNavigation.Newer(new(0, 0), 1, _ => pages.Count)!.Value);
            }
        }
    }

    public static void DraftHeightAndLosslessPages()
    {
        // Destination has a higher DPI than the owning mascot, and the HWND
        // client is shorter than the requested window. The old owner-scale
        // budget is too generous for this actual client.
        const double ownerScale = 1.25, destinationScale = 1.75, navHeight = 44;
        const int requestedHeight = 451, actualHeight = 367;
        var destination = BubbleLayout.ClientDip(requestedHeight, actualHeight, destinationScale);
        var budget = BubbleLayout.ConversationBodyBudget(destination, navHeight, destinationScale);
        var oldBudget = requestedHeight / ownerScale - navHeight - 2;
        True(oldBudget * destinationScale > actualHeight, "Exercise prior unit/viewport mismatch.");
        True((budget + BubbleLayout.RoundUp(navHeight, destinationScale) + 2) * destinationScale
            <= actualHeight + 1e-8, "Content, navigation and safety insets stay inside physical client pixels.");
        Equal(requestedHeight / destinationScale,
            BubbleLayout.ClientDip(requestedHeight, requestedHeight + 200, destinationScale));
        Equal(0d, BubbleLayout.ConversationBodyBudget(20, navHeight, destinationScale));

        var text = string.Join("\r\n", Enumerable.Range(1, 10).Select(i =>
            $"{i:D2} 合成の検証文です。日本語とABC123を混ぜた段落を折り返して表示します。次の行に続く文章を最後まで読めるか確認します。"))
            + "\r\n最終行を含む。";
        True(text.Length is >= 550 and <= 750, "Exercise a realistic long draft without private text.");
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            const double minimum = 44, feedback = 29;
            foreach (var side in Enum.GetValues<OverlaySide>())
            {
                var chrome = BubbleLayout.Vertical(side) ? 84 : 72;
                var reserve = BubbleLayout.HistoryChrome(side, 6, scale) + minimum + 1 / scale;
                double Measure(string value) => BubbleLayout.HistoryBodyHeight(8 +
                    value.Split('\n').Sum(line => (double)Math.Ceiling(Math.Max(1, line.Length) / 28.0) * 20), scale);
                var high = BubbleLayout.ComposerBodyBudget(960, chrome, feedback, minimum, reserve, scale);
                var low = BubbleLayout.ComposerBodyBudget(240, chrome, feedback, minimum, reserve, scale);

                Equal(1, TextPaging.Split(text, high, Measure).Count);
                True(high + chrome + feedback + reserve <= 960 + 1e-8,
                    "A fitting draft can use more than a fixed third and leave a readable history slot.");
                var pages = TextPaging.Split(text, low, Measure);
                True(pages.Count > 1 && pages.All(page => page.Height + chrome + feedback <= 240 + 1e-8),
                    "Constrained draft pages fit their work area, including chrome and warnings.");
                Equal(text, string.Concat(pages.Select(page => page.Read(text))));
                True(BubbleLayout.ComposerBodyBudget(chrome + feedback + minimum + 2, chrome,
                    feedback, minimum, reserve, scale) >= minimum,
                    "Keep one editable line if both surfaces cannot fit together.");
            }
        }
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

    public static void IndependentVisibility()
    {
        var none = default(ConversationVisibility);
        var input = none.ToggleInput();
        Equal(new ConversationVisibility(true, false), input);
        var both = input.ToggleHistory();
        Equal(new ConversationVisibility(true, true), both);
        var history = both.ToggleInput();
        Equal(new ConversationVisibility(false, true), history);
        Equal(none, history.ToggleHistory());
        Equal(history, none.ToggleHistory());
        Equal(both, history.ToggleInput());
        Equal(input, both.ToggleHistory());
        Equal(none, input.ToggleInput());
        True(!none.Any && input.Any && history.Any && both.Any, "Any visible panel enables the mascot's all-close action.");
        Equal(both, none.ToggleAll());
        Equal(none, input.ToggleAll());
        Equal(none, history.ToggleAll());
        Equal(none, both.ToggleAll());
    }

    public static void RetainedHistoryCards()
    {
        var remove = RetainedSequence.Plan(new[] { "old", "middle", "new" }, new[] { "middle", "new" });
        Equal(1, remove.Count);
        Equal(new SequenceEdit<string>(true, 0, "old"), remove[0]);
        var prepend = RetainedSequence.Plan(new[] { "middle", "new" }, new[] { "old", "middle", "new" });
        Equal(1, prepend.Count);
        Equal(new SequenceEdit<string>(false, 0, "old"), prepend[0]);
        var shift = RetainedSequence.Plan(new[] { "a", "b", "c" }, new[] { "b", "c", "d" });
        True(shift.SequenceEqual(new[] { new SequenceEdit<string>(true, 0, "a"),
            new SequenceEdit<string>(false, 2, "d") }), "Keep both retained cards when the visible range shifts.");
        Equal(0, RetainedSequence.Plan(new[] { "a", "b" }, new[] { "a", "b" }).Count);
        var read = RetainedSequence.Plan(new[] { "a", "b-unread", "c" }, new[] { "a", "b-read", "c" });
        True(read.SequenceEqual(new[] { new SequenceEdit<string>(true, 1, "b-unread"),
            new SequenceEdit<string>(false, 1, "b-read") }), "Only the changed card is replaced.");
    }

    public static void NearbyAuxiliaryWindows()
    {
        foreach (var (area, mascot) in new[]
        {
            (new ScreenRect(-2560, -60, 2560, 1400), new ScreenRect(-205, 1070, 180, 260)),
            (new ScreenRect(0, 0, 1920, 1040), new ScreenRect(16, 764, 177, 260)),
            (new ScreenRect(1920, -900, 1600, 860), new ScreenRect(3320, -310, 177, 260))
        })
        {
            var zone = OverlayLayout.ConversationZone(area, mascot, 1);
            var statusWidth = OverlayLayout.StatusWidth(area, mascot);
            Equal(mascot.Width, statusWidth);
            var status = OverlayLayout.StatusPlacement(area, mascot, zone, statusWidth, 180, 8, conversationVisible: true);
            True(status.HasValue, "Status fits directly adjacent to the mascot.");
            var s = status!.Value;
            True(s.X >= area.X && s.Y >= area.Y && s.Right <= area.Right && s.Bottom <= area.Bottom,
                "Status remains inside its work area.");
            True(!OverlayLayout.Intersects(s, mascot) && !OverlayLayout.Intersects(s, zone.Bounds),
                "Status avoids the mascot and the reserved conversation zone when space permits.");
            True(Distance(s, mascot) <= 8, "No gap for a removed hover menu.");
            Equal(status, OverlayLayout.StatusPlacement(area, mascot, zone, statusWidth, 180, 8, conversationVisible: true));
            if (mascot.Bottom + 8 + 180 <= area.Bottom)
                Equal(mascot.Bottom + 8, s.Y);
        }

        // Bottom-edge mascots cannot fit a status panel below; it should be
        // immediately above, not above a now-removed hover slot.
        var bottomArea = new ScreenRect(0, 0, 1920, 1040);
        var bottomMascot = new ScreenRect(1725, 764, 177, 260);
        var bottomZone = OverlayLayout.ConversationZone(bottomArea, bottomMascot, 1);
        var bottomStatus = OverlayLayout.StatusPlacement(bottomArea, bottomMascot, bottomZone, OverlayLayout.StatusWidth(bottomArea, bottomMascot), 80, 8, conversationVisible: true);
        True(bottomStatus.HasValue, "Status fits immediately above the bottom-edge mascot.");
        Equal(bottomMascot.Y - 8, bottomStatus!.Value.Bottom);

        // Large empty corners are deliberately not valid fallback targets.
        var blocked = OverlayLayout.NearbyAuxiliary(new ScreenRect(0, 0, 1000, 800),
            new ScreenRect(450, 350, 100, 100), 120, 60, 8, OverlaySide.Above,
            new[] { new ScreenRect(0, 250, 1000, 300) });
        True(blocked is null, "An unavailable adjacent slot must not jump to a remote corner.");
    }

    public static void MascotStatusWidth()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var area = new ScreenRect(-2560, -60, 2560, 1440);
            int Px(double dip) => (int)Math.Ceiling(dip * scale);
            foreach (var height in new[] { 120, 180, 260 })
            {
                var width = (int)Math.Ceiling(height * 0.68 * scale);
                foreach (var x in new[] { area.X, area.Right - width, area.X + 600 })
                {
                    var anchor = new ScreenRect(x, area.Bottom - Px(height), width, Px(height));
                    var zone = OverlayLayout.ConversationZone(area, anchor, scale);
                    var actual = OverlayLayout.StatusWidth(area, anchor);
                    Equal(width, actual);
                    var status = OverlayLayout.StatusPlacement(area, anchor, zone, actual, Px(160), Px(8),
                        conversationVisible: true);
                    True(status.HasValue, "Normal work area has a nearby status slot.");
                    Equal(width, status!.Value.Width);
                    Equal(anchor.X, status.Value.X);
                    True(!OverlayLayout.Intersects(status.Value, anchor) &&
                        !OverlayLayout.Intersects(status.Value, zone.Bounds), "Status must not obscure mascot or conversation.");
                }
            }
        }
        // Only an overlapping slot exists. Use it only if the conversation is closed.
        var cramped = new ScreenRect(0, 0, 200, 180);
        var mascot = new ScreenRect(0, 60, 80, 120);
        var conversation = new OverlayPlacement(new ScreenRect(88, 0, 112, 180), OverlaySide.Right);
        var statusWidth = OverlayLayout.StatusWidth(cramped, mascot);
        True(OverlayLayout.StatusPlacement(cramped, mascot, conversation, statusWidth, 100, 8,
            conversationVisible: true) is null, "Do not cover open conversation.");
        True(OverlayLayout.StatusPlacement(cramped, mascot, conversation, statusWidth, 100, 8,
            conversationVisible: false).HasValue, "Allow the free space when conversation is closed.");
    }

    private static int Distance(ScreenRect a, ScreenRect b)
    {
        var x = Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right));
        var y = Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom));
        return x + y;
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
