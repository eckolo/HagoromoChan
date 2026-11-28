using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using HagoromoChan.LayoutChecks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HagoromoChan.Contracts;
using HagoromoChan.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // A real Desktop App schedules OnStartup on the dispatcher. The
        // Loaded retry below pumps that dispatcher; production startup here
        // would open the user's data directory and take its instance lock.
        // Load the same styles without ever constructing a production App.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/HagoromoChan.Desktop;component/DesktopResources.xaml", UriKind.Relative)
        });
        CheckLocalMotionResources();
        var type = typeof(App).Assembly.GetType("HagoromoChan.Desktop.DesktopPanels", true)!;
        var owner = new Window { WindowStartupLocation = WindowStartupLocation.Manual, Width = 132, Height = 140, ShowActivated = false };
        var panels = Activator.CreateInstance(type, owner, (Action)(() => { }),
            (Action)(() => { }), (Action<IReadOnlyList<string>>)(_ => { }), (Action)(() => { }))!;
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object? Invoke(string name, params object[] args) => type.GetMethod(name, Flags)!.Invoke(panels, args);
        var window = (Window)type.GetProperty("Conversation")!.GetValue(panels)!;
        var root = (FrameworkElement)window.Content;
        var stack = (Panel)type.GetField("_historyStack", Flags)!.GetValue(panels)!;
        var editor = (TextBox)type.GetProperty("MessageInput")!.GetValue(panels)!;
        var desktop = type.Assembly.GetType("HagoromoChan.Desktop.NativeDesktop", true)!;
        var sideType = type.Assembly.GetType("HagoromoChan.Desktop.OverlaySide", true)!;
        var layout = type.Assembly.GetType("HagoromoChan.Desktop.BubbleLayout", true)!;
        // A history card can be measured before its theme template exists.
        // Keep the startup regression in the same Windows conversation test.
        Phase("History metrics before template");
        var measuringBody = (TextBox)type.GetMethod("HistoryBodyBox", Flags | BindingFlags.Static)!.Invoke(null, null)!;
        measuringBody.Template = null;
        var metricsType = type.Assembly.GetType("HagoromoChan.Desktop.HistoryBodyMeasurement", true)!;
        var metrics = metricsType.GetMethod("Measure")!.Invoke(null,
            [measuringBody, "一行目\r\n二行目\r\n三行目\r\n四行目", 220d, new DpiScale(1, 1), 3])!;
        var previews = (Array)metricsType.GetProperty("Excerpts")!.GetValue(metrics)!;
        var full = (double)metricsType.GetProperty("FullHeight")!.GetValue(metrics)!;
        Require(measuringBody.Template is null && previews.Length == 3 && double.IsFinite(full) && full > 0,
            "An untemplated history body could not produce three whole-line previews.");
        var third = previews.GetValue(2)!;
        var preview = (string)third.GetType().GetProperty("Text")!.GetValue(third)!;
        var previewHeight = (double)third.GetType().GetProperty("Height")!.GetValue(third)!;
        Require(preview == "一行目\r\n二行目\r\n三行目" && previewHeight > 0 && previewHeight < full,
            "Pre-template formatting miscounted explicit line breaks or the expanded/preview heights.");

        Phase("owner.Show");
        owner.Show();
        Phase("owner.Show completed");
        try
        {
            var area = desktop.GetMethod("WorkArea", [typeof(Window)])!.Invoke(null, [owner])!;
            int Px(object rect, string name) => (int)rect.GetType().GetProperty(name)!.GetValue(rect)!;
            var edge = Activator.CreateInstance(area.GetType(), [
                Px(area, "Right") - 136, Px(area, "Bottom") - 144, 132, 140])!;
            desktop.GetMethod("SetBounds")!.Invoke(null, [owner, edge]);
            Phase("panels.Attach");
            Invoke("Attach");
            Phase("panels.Attach completed");
            void Layout(string stage)
            {
                Phase(stage + ": UpdateLayout");
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                Phase(stage + ": layout settled");
            }
            var topic = string.Join("\r\n", Enumerable.Range(1, 10).Select(i =>
                $"{i:D2} 合成の検証文です。日本語とABC123を混ぜた段落を折り返して表示します。次の行に続く文章を最後まで読めるか確認します。"))
                + "\r\n最終行：語😀 gypqç";
            Require(topic.Length is >= 550 and <= 750, "Keep the production fixture near 600 characters.");
            var longText = topic + "\r\n" + string.Concat(Enumerable.Repeat("ABC123折り返し😀", 350)) + "\r\n末尾gypq語\r\n";
            var history = new[]
            {
                new ChatMessage(1, "topic-user", "user", topic, null, DateTimeOffset.UnixEpoch),
                new ChatMessage(2, "topic-assistant", "assistant", topic, "topic-user", DateTimeOffset.UnixEpoch),
                new ChatMessage(3, "long", "assistant", longText, "topic-user", DateTimeOffset.UnixEpoch)
            };
            Invoke("UpdateHistory", history, Array.Empty<string>());

            (DockPanel Card, Border Frame, TextBox Body) Active()
            {
                Require(stack.Children.Count > 0, "Production history card disappeared.");
                var card = (DockPanel)stack.Children[0];
                var frame = (Border)card.Children[1];
                return (card, frame, (TextBox)((DockPanel)frame.Child).Children[1]);
            }
            void ExerciseNativeTextHit(TextBox box)
            {
                // The production-shaped, attached read-only TextBox must still
                // support point-to-text and selection. UI Automation invokes
                // native hit-testing without moving the operator's pointer.
                var peer = new TextBoxAutomationPeer(box);
                var provider = peer.GetPattern(PatternInterface.Text) as ITextProvider;
                Require(provider is not null, "The retained history body lost its native text provider.");
                Phase("history UIA RangeFromPoint");
                var pixel = box.PointToScreen(new Point(box.ActualWidth / 4, box.ActualHeight / 3));
                var hit = provider!.RangeFromPoint(pixel);
                Require(hit is not null, "The native history TextBox cannot hit-test its live attached viewport.");
                hit!.ExpandToEnclosingUnit(TextUnit.Character);
                hit.Select();
                Require(box.SelectionLength > 0, "Point-to-text did not select content in the retained TextBox.");
                var emoji = box.Text.IndexOf("😀", StringComparison.Ordinal);
                Require(emoji >= 0, "The original Unicode fixture lost its emoji.");
                var selectionStart = box.SelectionStart;
                var selectionLength = box.SelectionLength;
                box.Select(emoji, "😀".Length);
                Require(box.SelectedText == "😀", "The live native control did not retain the original emoji for selection/copy.");
                box.Select(selectionStart, selectionLength);
                Phase("history UIA selection completed");
            }
            void InsideClient(FrameworkElement element)
            {
                var (nativeWidth, nativeHeight) = ((int, int))desktop.GetMethod("ClientSize")!.Invoke(null, [window])!;
                var scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
                var point = element.TranslatePoint(new Point(0, 0), root);
                Require(point.X >= -1 / scale && point.Y >= -1 / scale &&
                    (point.X + element.ActualWidth) * scale <= nativeWidth + 1 &&
                    (point.Y + element.ActualHeight) * scale <= nativeHeight + 1,
                    "A production bubble escapes the native HWND client.");
            }

            foreach (var id in new[] { "topic-user", "topic-assistant" })
            {
                Phase("ShowMessage " + id);
                Invoke("ShowMessage", id);
                Layout(id);
                var bounds = desktop.GetMethod("Bounds")!.Invoke(null, [window])!;
                Require(Px(bounds, "X") >= Px(area, "X") && Px(bounds, "Y") >= Px(area, "Y") &&
                    Px(bounds, "Right") <= Px(area, "Right") && Px(bounds, "Bottom") <= Px(area, "Bottom"),
                    "The native HWND escaped the work area.");
                var (card, _, body) = Active();
                InsideClient(card);
                Require(body.Text == topic && body.IsReadOnly, "The production fixture changed its full source.");
                if (id == "topic-user")
                {
                    // Both characters belong to the very same logical paragraph.
                    // Extent/line count alone would pass because other paragraphs
                    // already contain explicit CRLFs. The Japanese paragraph is
                    // intentionally wider than the maximally sized body at the
                    // production font; do not artificially narrow this window.
                    // Compare actual caret rows, not a nominal pixel estimate.
                    Phase("Native soft-wrap in the first visible paragraph");
                    var paragraphEnd = body.Text.IndexOf('\r');
                    Require(paragraphEnd > 1 && !body.Text[..paragraphEnd].Contains('\n'),
                        "The soft-wrap fixture needs one uninterrupted first paragraph.");
                    Require(body.VerticalOffset == 0, "The first paragraph is not at the viewport origin.");
                    var presenter = FindPresenter(body);
                    Require(presenter is not null, "The native text viewport is missing.");
                    var viewport = presenter!.TransformToAncestor(body).TransformBounds(new Rect(new Point(), presenter.RenderSize));
                    var firstRect = body.GetRectFromCharacterIndex(0, trailingEdge: false);
                    var lastRect = body.GetRectFromCharacterIndex(paragraphEnd - 1, trailingEdge: true);
                    Require(!firstRect.IsEmpty && !lastRect.IsEmpty && firstRect.Top >= viewport.Top &&
                        lastRect.Bottom <= viewport.Bottom && lastRect.Top > firstRect.Top,
                        $"Soft wrap not visible: first={firstRect}, last={lastRect}, viewport={viewport}.");
                    Phase("Native soft-wrap completed");
                }
                body.ScrollToEnd();
                Layout(id + " native end");
                CheckTailInk(window, body, "gypqç", verifyNegativeControl: id == "topic-user");
                ExerciseNativeTextHit(body);
            }

            Phase("ShowMessage long");
            Invoke("ShowMessage", "long");
            Layout("long initial");
            var (_, _, arrowBody) = Active();
            Require(arrowBody.Text == longText && arrowBody.ExtentHeight > arrowBody.ViewportHeight,
                "Long fixture did not require a real native scroll viewport.");
            var topBefore = arrowBody.VerticalOffset;
            Invoke("MoveHistory", 1);
            Layout("long down");
            Require(ReferenceEquals(Active().Body, arrowBody) && arrowBody.VerticalOffset > topBefore,
                "Production down arrow advanced the wrong message instead of scrolling its native body.");
            Invoke("MoveHistory", -1);
            Layout("long up");
            Require(Math.Abs(arrowBody.VerticalOffset - topBefore) < .01, "Native up/down was not reversible.");

            var scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
            var width = (double)layout.GetMethod("BodyWidth")!.Invoke(null, [root.ActualWidth, scale])!;
            double? x = null;
            foreach (var side in new[] { "Right", "Left" })
            {
                Invoke("RenderHistory", width, (double)root.ActualHeight, Enum.Parse(sideType, side), scale);
                Layout("Bubble tail side: " + side);
                var (_, frame, body) = Active();
                var origin = frame.TranslatePoint(new Point(0, 0), root).X;
                x ??= origin;
                Require(Math.Abs(origin - x.Value) <= 1 / scale, "The bubble frame moves when only the tail switches sides.");
                Require(ReferenceEquals(body, arrowBody), "The bubble frame change recreated the retained native body.");
            }
            // Continue through the real arrow handler with no selection: this
            // is the failing production path, not merely a direct API jump.
            RequirePhysicalScrollOwner(arrowBody, "History");
            var newer = (RepeatButton)type.GetField("_newer", Flags)!.GetValue(panels)!;
            var steps = 0;
            while (arrowBody.ExtentHeight - arrowBody.ViewportHeight - arrowBody.VerticalOffset > .01)
            {
                Require(++steps <= longText.Length && newer.IsEnabled && arrowBody.SelectionLength == 0,
                    "The long, unselected history did not advance to its end.");
                var before = arrowBody.VerticalOffset;
                Phase($"long unselected arrow {steps}");
                newer.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, newer));
                Layout($"long unselected arrow {steps}");
                Require(ReferenceEquals(Active().Body, arrowBody) && arrowBody.VerticalOffset > before,
                    "The actual arrow must advance the same native history viewport.");
            }
            Require(steps > 0, "The long history did not exercise the actual arrow.");
            Phase($"long unselected arrow reached the native end in {steps} steps");
            CheckTailInk(window, arrowBody, "末尾gypq語", verifyNegativeControl: false);
            arrowBody.ScrollToHome();
            Layout("return to long origin before selection");
            arrowBody.Select(0, 3);
            arrowBody.ScrollToEnd();
            Layout("long terminal");
            var (longCard, _, retainedBody) = Active();
            InsideClient(longCard);
            Require(ReferenceEquals(arrowBody, retainedBody) && retainedBody.SelectionLength == 3 &&
                retainedBody.Text == longText && retainedBody.VerticalOffset > 0 &&
                Math.Abs(retainedBody.VerticalOffset - (retainedBody.ExtentHeight - retainedBody.ViewportHeight)) <= 1 / scale,
                "Native terminal scrolling lost the original Unicode text, selection or final extent.");
            retainedBody.Select(0, 0);
            CheckTailInk(window, retainedBody, "末尾gypq語", verifyNegativeControl: true);
            ExerciseNativeTextHit(retainedBody);

            Phase("history: constrained viewport");
            var room = root.ActualHeight;
            root.GetType().GetMethod("SetClientBudget", Flags)!.Invoke(root, [root.ActualWidth, 220.0]);
            Layout("long constrained client");
            Require(ReferenceEquals(Active().Body, retainedBody) && retainedBody.ViewportHeight > 0 &&
                retainedBody.ViewportHeight < retainedBody.ExtentHeight && root.ActualHeight <= 220.0 + 1 / scale,
                "The smaller layout did not retain the same bounded native document.");
            retainedBody.ScrollToEnd();
            Layout("long constrained terminal");
            InsideClient(Active().Card);
            CheckTailInk(window, retainedBody, "末尾gypq語", verifyNegativeControl: false);
            root.GetType().GetMethod("SetClientBudget", Flags)!.Invoke(root, [root.ActualWidth, room]);
            Layout("restore native client");

            retainedBody.ScrollToHome();
            Layout("history older initial cursor");
            Invoke("MoveHistory", -1);
            Layout("history older document");
            var previous = Active().Body;
            Require(topic.StartsWith(previous.Text, StringComparison.Ordinal) && previous.Text != topic &&
                previous.LineCount == 3 && previous.VerticalOffset <= 1 / scale,
                "Older history did not return as a whole-line preview at its beginning.");
            object Disclosure()
            {
                var (_, frame, _) = Active();
                var header = (DockPanel)((DockPanel)frame.Child).Children[0];
                return header.Children.OfType<Button>().Single(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) is "全文を表示" or "折りたたむ");
            }
            void ToggleDisclosure()
            {
                var button = (Button)Disclosure();
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                Layout("history disclosure");
            }
            ToggleDisclosure();
            Require(ReferenceEquals(Active().Body, previous) && previous.Text == topic,
                "The same older card should restore all the original text when expanded.");
            // A native resize can perform intermediate layouts before the
            // dispatcher reaches the stable view. None is a page navigation.
            var historyBudgetSetter = root.GetType().GetMethod("SetClientBudget", Flags)!;
            var historyWidth = root.ActualWidth;
            historyBudgetSetter.Invoke(root, [historyWidth, 0.0]);
            window.UpdateLayout();
            historyBudgetSetter.Invoke(root, [historyWidth, room]);
            Layout("restore viewport before display commit");
            Invoke("UpdateHistory", history, new[] { "long" });
            Layout("history refresh after disclosure");
            Require(ReferenceEquals(Active().Body, previous) && previous.Text == topic,
                "A transient empty layout closed the same expanded history page on refresh.");
            CheckTailInkAfterScroll(previous, "gypqç");
            ToggleDisclosure();
            Invoke("SetHistoryPreviewLines", 2);
            Layout("history preview setting");
            Require(Active().Body.LineCount == 2 && Active().Body.Text != topic,
                "Changing the preview preference did not apply to a collapsed history card.");
            ToggleDisclosure();
            Require(Active().Body.Text == topic, "The expanded card did not preserve the full source.");
            // Opening the recent message advances the anchor without changing the
            // other card; shrink enough that it really leaves the viewport.
            Invoke("ShowMessage", "long");
            Layout("history newer document");
            Require(stack.Children.Count == 1, "Returning to the newest reply unexpectedly added earlier cards.");
            Require(Active().Body.Text == longText && Active().Body.VerticalOffset <= 1 / scale,
                "The newer expanded item did not start at its native beginning.");
            Invoke("MoveHistory", -1);
            Layout("revisit older history");
            Require(Active().Body.Text != topic && Active().Body.LineCount == 2,
                "The older expanded card did not re-collapse after leaving the viewport.");
            Invoke("ShowMessage", "long");
            Layout("resume recent history");

            void CheckTailInkAfterScroll(TextBox body, string suffix)
            {
                body.ScrollToEnd();
                Layout("expanded history terminal");
                CheckTailInk(window, body, suffix, verifyNegativeControl: false);
            }

            Phase("SetDraftText and OpenBoth");
            Invoke("SetDraftText", longText);
            Invoke("OpenBoth");
            Layout("draft initial");
            var composer = (FrameworkElement?)type.GetField("_composerBubble", Flags)!.GetValue(panels);
            Require(composer?.IsVisible == true, "Live composer missing.");
            InsideClient(composer!);
            RequirePhysicalScrollOwner(editor, "Draft");
            Require(editor.Text == longText && editor.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden,
                "Composer no longer contains the full original text or gained a width-changing scrollbar.");
            Phase("Draft: move the caret and scroll to the end");
            editor.CaretIndex = editor.Text.Length;
            editor.ScrollToEnd();
            Layout("draft terminal");
            CheckDraftEnd(editor);
            Phase("Draft: edit and undo at the end");
            const string addition = "追記😀";
            editor.SelectedText = addition;
            editor.CaretIndex = editor.Text.Length;
            Layout("draft after editing");
            Require(editor.Text == longText + addition &&
                Equals(type.GetProperty("DraftText")!.GetValue(panels), editor.Text),
                "The native edit did not preserve the full draft and notify the application.");
            CheckDraftEnd(editor);
            editor.Undo();
            Layout("draft after undo");
            Require(editor.Text == longText && Equals(type.GetProperty("DraftText")!.GetValue(panels), longText),
                "Undo did not restore the full draft.");
            Phase("Native Markdown history and original view");
            Invoke("CloseComposer");
            const string safeLink = "[確認](https://example.invalid/a)";
            const string inertImage = "![画像](https://example.invalid/image.png)";
            const string inertMarkup = "<script>example</script>";
            var markdown = "# 日本語の見出し 😀\r\n\r\n" +
                "- **太字**・*強調*・`inline`\r\n- " + safeLink + " と " + inertImage + "\r\n\r\n" +
                "> 引用 " + inertMarkup + "\r\n\r\n" +
                "```txt\r\n" + string.Concat(Enumerable.Repeat("ABC123日本語😀", 80)) + "\r\n```\r\n\r\n" +
                string.Join("\r\n\r\n", Enumerable.Repeat("日本語の長文を狭い会話窓でも読めます。", 26)) +
                "\r\n\r\n書式の末尾語";
            Invoke("UpdateHistory", new[]
            {
                new ChatMessage(4, "rich", "assistant", markdown, null, DateTimeOffset.UnixEpoch),
                new ChatMessage(5, "after-rich", "assistant", "次の発言", null, DateTimeOffset.UnixEpoch)
            }, new[] { "rich" });
            Invoke("ShowMessage", "rich");
            Layout("rich history");
            (DockPanel Card, DockPanel Content) ActiveContent()
            {
                var card = (DockPanel)stack.Children[0];
                return (card, (DockPanel)((Border)card.Children[1]).Child);
            }
            Button HistoryButton(string name) => ((DockPanel)ActiveContent().Content.Children[0]).Children
                .OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);
            void ClickHistoryButton(string name)
            {
                var button = HistoryButton(name);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                Layout(name);
            }
            var headerNames = new[] { "この回答を既読にする", "原文を表示", "折りたたむ", "この発言の全文をコピー" };
            void CheckHistoryHeader(string[] expected)
            {
                var header = (DockPanel)ActiveContent().Content.Children[0];
                var names = header.Children.OfType<Button>()
                    .Where(button => button.IsVisible)
                    .OrderBy(button => button.TranslatePoint(new Point(0, 0), header).X)
                    .Select(button => System.Windows.Automation.AutomationProperties.GetName(button))
                    .ToArray();
                Require(names.SequenceEqual(expected), "History header actions were not in the expected visual order.");
            }
            CheckHistoryHeader(headerNames);
            var rich = ActiveContent().Content.Children[1] as RichTextBox;
            Require(rich is { IsReadOnly: true, IsDocumentEnabled: false },
                "Supported Markdown did not open by default as a read-only native document.");
            RichTextBox formatted = rich!;
            Require(formatted.Document.Blocks.FirstBlock is Paragraph first && first.FontWeight == FontWeights.Bold,
                "The Markdown heading was not rendered as a native heading.");
            Require(formatted.Document.Blocks.OfType<System.Windows.Documents.List>().Count() == 1,
                "The Markdown list was not rendered with native list structure.");
            var shown = new TextRange(formatted.Document.ContentStart, formatted.Document.ContentEnd).Text;
            Require(shown.Contains(safeLink, StringComparison.Ordinal) && shown.Contains(inertImage, StringComparison.Ordinal) &&
                shown.Contains(inertMarkup, StringComparison.Ordinal) && shown.Contains("書式の末尾語", StringComparison.Ordinal),
                "The native Markdown contained missing or activated external syntax.");
            ClickHistoryButton("原文を表示");
            Require(Active().Body.Text == markdown, "The original view lost Markdown syntax or text.");
            ClickHistoryButton("書式付きで表示");
            Require(ReferenceEquals(ActiveContent().Content.Children[1], formatted),
                "A format toggle replaced the retained native document.");
            // Recreating the card after read-state changes must not be required
            // to hide the read action, or to retain the order of the others.
            Invoke("UpdateHistory", new[]
            {
                new ChatMessage(4, "rich", "assistant", markdown, null, DateTimeOffset.UnixEpoch),
                new ChatMessage(5, "after-rich", "assistant", "次の発言", null, DateTimeOffset.UnixEpoch)
            }, Array.Empty<string>());
            Layout("Markdown read-state controls");
            Require(ReferenceEquals(ActiveContent().Content.Children[1], formatted),
                "Changing unread state replaced the native document.");
            CheckHistoryHeader(headerNames[1..]);
            // A tall desktop can fit the entire formatted fixture. Use the same
            // production client constraint as the plain-history regression, so
            // this check exercises overflow regardless of monitor height.
            var markdownClient = root.RenderSize;
            const double markdownHeight = 300.0;
            historyBudgetSetter.Invoke(root, [markdownClient.Width, markdownHeight]);
            Layout("Markdown constrained client");
            InsideClient(ActiveContent().Card);
            Require(ReferenceEquals(ActiveContent().Content.Children[1], formatted) &&
                root.ActualHeight <= markdownHeight + 1 / scale,
                "The Markdown card was not retained in the constrained client.");
            RequirePhysicalScrollOwner(formatted, "Markdown history");
            Console.WriteLine(FormattableString.Invariant(
                $"TRACE conversation layout: Markdown constrained viewport={formatted.ViewportHeight:F2}, extent={formatted.ExtentHeight:F2}, offset={formatted.VerticalOffset:F2}"));
            Require(formatted.ViewportHeight > 0 && formatted.ExtentHeight - formatted.ViewportHeight > 1 / scale,
                "The long Markdown did not expose a native scroll range in the constrained client.");
            var markdownNext = (RepeatButton)type.GetField("_newer", Flags)!.GetValue(panels)!;
            void ClickArrow(RepeatButton arrow)
            {
                arrow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, arrow));
                Layout("history arrow");
            }
            for (int i = 0; i < 250 && formatted.ExtentHeight - formatted.ViewportHeight - formatted.VerticalOffset > .01; i++)
            {
                var before = formatted.VerticalOffset;
                ClickArrow(markdownNext);
                Require(ReferenceEquals(ActiveContent().Content.Children[1], formatted) &&
                    formatted.VerticalOffset > before,
                    "The arrow failed to advance the same formatted message before its body tail.");
            }
            var richViewport = FindPresenter(formatted)!;
            var richView = richViewport.TransformToAncestor(formatted)
                .TransformBounds(new Rect(new Point(), richViewport.RenderSize));
            var endRect = formatted.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
                .GetCharacterRect(LogicalDirection.Backward);
            var dip = 1 / VisualTreeHelper.GetDpi(formatted).DpiScaleY;
            Require(formatted.VerticalOffset > 0 && formatted.ExtentHeight - formatted.ViewportHeight - formatted.VerticalOffset <= dip &&
                !endRect.IsEmpty && endRect.Top >= richView.Top - dip && endRect.Bottom <= richView.Bottom + dip,
                "The last formatted row was not visible at the native scroll tail.");
            ClickArrow(markdownNext);
            Require(Active().Body.Text == "次の発言", "The arrow at the native Markdown tail did not move to the next message.");
            var older = (RepeatButton)type.GetField("_older", Flags)!.GetValue(panels)!;
            ClickArrow(older);
            Require(Active().Body.Text != markdown && markdown.StartsWith(Active().Body.Text, StringComparison.Ordinal),
                "Returning to the old Markdown card did not preserve its plain collapsed preview.");
            historyBudgetSetter.Invoke(root, [markdownClient.Width, markdownClient.Height]);
            Layout("restore Markdown client");

            Phase("Markdown source view lifetime");
            void ShowFormattedHistory(string stage)
            {
                Invoke("ShowMessage", "rich");
                Layout(stage);
                Require(ActiveContent().Content.Children[1] is RichTextBox,
                    "Returning to an expanded Markdown card did not restore its default formatted view.");
            }
            ShowFormattedHistory("initial formatted view");
            ClickHistoryButton("原文を表示");
            historyBudgetSetter.Invoke(root, [markdownClient.Width, 0.0]);
            window.UpdateLayout();
            historyBudgetSetter.Invoke(root, [markdownClient.Width, markdownClient.Height]);
            Layout("source view after intermediate relayout");
            Require(Active().Body.Text == markdown, "Intermediate layout reset the visible card's source view.");
            Invoke("HideSurfaces");
            Layout("hide conversation");
            ShowFormattedHistory("reopen conversation");
            ClickHistoryButton("原文を表示");
            Invoke("ShowMessage", "after-rich");
            Layout("leave the source card");
            Require(Active().Body.Text == "次の発言", "The source card did not leave the visible history.");
            ShowFormattedHistory("revisit the source card");

            Console.WriteLine("PASS conversation layout: history rendering, navigation and editable draft");
        }
        finally
        {
            Invoke("PrepareForShutdown");
            if (window.IsLoaded) window.Close();
            if (owner.IsVisible) owner.Close();
        }
    }

    private static void CheckLocalMotionResources()
    {
        var assembly = typeof(App).Assembly;
        var model = assembly.GetType("HagoromoChan.Desktop.LocalMotionCells", true)!;
        var keys = (IReadOnlyList<string>)model.GetProperty("Keys")!.GetValue(null)!;
        var type = assembly.GetType("HagoromoChan.Desktop.CharacterReactionArtwork", true)!;
        var uri = new Uri("pack://application:,,,/HagoromoChan.Desktop;component/Assets/Mascot.png");
        var original = new BitmapImage(uri);
        var cleanDirectory = Directory.CreateTempSubdirectory("local-action-art-test-");
        try
        {
            var artwork = Activator.CreateInstance(type, original, cleanDirectory.FullName, true)!;
            var frame = type.GetMethod("Frame")!;
            var idle = type.GetProperty("RestingImage")!.GetValue(artwork)!;
            foreach (var key in keys)
            for (var cell = 0; cell < 3; cell++)
            {
                var image = (DrawingImage)frame.Invoke(artwork, [key, 0d, 0d, 0d, cell])!;
                Require(!ReferenceEquals(image, idle), $"unexpected fallback for {key}/{cell}");
                var child = (ImageDrawing)((DrawingGroup)image.Drawing).Children[1];
                var crop = (CroppedBitmap)child.ImageSource;
                Require(crop.SourceRect == new Int32Rect(288 * cell, 0, 288, 368), $"wrong source cell {key}/{cell}");
                var scale = child.Rect.Width / 288;
                Require(Math.Abs(child.Rect.X + 144 * scale - original.PixelWidth / 2.0) < .001 &&
                    Math.Abs(child.Rect.Y + 360 * scale - (original.PixelHeight - 8 * scale)) < .001,
                    $"foot anchor mismatch: {key}/{cell}");
            }
            Require(keys.ToHashSet(StringComparer.Ordinal).SetEquals(new[]
                { "startup-bow", "head-tilt-left", "head-tilt-right", "peek-left", "peek-right", "conversation-close" }),
                "all local-action sheets are mapped");
            foreach (var height in new[] { 120d, 180d, 260d })
            foreach (var dpiScale in new[] { 1d, 1.25, 1.5, 2d })
            {
                type.GetMethod("SetDisplayScale")!.Invoke(artwork,
                    [Math.Min(1, height * dpiScale / original.PixelHeight)]);
                var neutral = (DrawingImage)type.GetProperty("RestingImage")!.GetValue(artwork)!;
                var expected = RenderLowerMascotPixels(neutral, original, height, dpiScale);
                Require(expected.Any(value => value != 0), "the resting lower-body comparison is not blank");
                for (var cell = 0; cell < 3; cell++)
                {
                    var bow = (DrawingImage)frame.Invoke(artwork, ["startup-bow", 0d, 0d, 0d, cell])!;
                    var neutralBody = (ImageDrawing)((DrawingGroup)neutral.Drawing).Children[1];
                    var bowBody = (ImageDrawing)((DrawingGroup)bow.Drawing).Children[1];
                    var a = neutralBody.Rect;
                    var b = bowBody.Rect;
                    Require(Math.Abs(a.X - b.X) < .001 && Math.Abs(a.Y - b.Y) < .001 &&
                        Math.Abs(a.Width - b.Width) < .001 && Math.Abs(a.Height - b.Height) < .001,
                        $"rest/bow drawing placement differs at {height}/{dpiScale}, cell {cell}");
                    Require(expected.AsSpan().SequenceEqual(RenderLowerMascotPixels(bow, original, height, dpiScale)),
                        $"rest/bow rendered foot pixels differ at {height}/{dpiScale}, cell {cell}");
                }
            }
        }
        finally { cleanDirectory.Delete(true); }
        Console.WriteLine("PASS local-action exact WPF resource, crop and anchor for every drawn cell");
    }

    private static byte[] RenderLowerMascotPixels(ImageSource source, BitmapSource original, double height, double dpiScale)
    {
        // Match the leading mascot Image in MainWindow; no production App startup or user data.
        var width = original.PixelWidth * height / original.PixelHeight;
        var image = new Image { Source = source, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly, UseLayoutRounding = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Measure(new Size(width, height));
        image.Arrange(new Rect(0, 0, width, height));
        image.UpdateLayout();
        var pixelWidth = (int)Math.Ceiling(width * dpiScale);
        var pixelHeight = (int)Math.Ceiling(height * dpiScale);
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        bitmap.Render(image);
        var lowerStart = pixelHeight * 3 / 4;
        var pixels = new byte[pixelWidth * (pixelHeight - lowerStart) * 4];
        bitmap.CopyPixels(new Int32Rect(0, lowerStart, pixelWidth, pixelHeight - lowerStart), pixels, pixelWidth * 4, 0);
        return pixels;
    }

    private static void RequirePhysicalScrollOwner(TextBoxBase body, string surface)
    {
        var presenter = FindPresenter(body);
        Require(presenter is { CanContentScroll: false, ScrollOwner: not null } &&
            presenter.ViewportHeight == body.ViewportHeight && presenter.ExtentHeight == body.ExtentHeight,
            $"{surface}: the native presenter does not own the scroll range.");
    }

    private static void CheckDraftEnd(TextBox editor)
    {
        var presenter = FindPresenter(editor)!;
        var view = presenter.TransformToAncestor(editor).TransformBounds(new Rect(new Point(), presenter.RenderSize));
        var end = editor.GetRectFromCharacterIndex(editor.Text.Length, trailingEdge: true);
        var pixel = 1 / VisualTreeHelper.GetDpi(editor).DpiScaleY;
        Require(editor.CaretIndex == editor.Text.Length && editor.ExtentHeight > editor.ViewportHeight &&
            editor.VerticalOffset > 0 && !end.IsEmpty && end.Top >= view.Top - pixel &&
            end.Bottom <= view.Bottom + pixel,
            $"The final draft caret is not visible: caret={end}, viewport={view}, offset={editor.VerticalOffset}.");
    }

    private static void Phase(string phase)
    {
        Console.WriteLine("TRACE conversation layout: " + phase);
        Console.Out.Flush();
    }

    private static void CheckTailInk(Window host, TextBox body, string finalLine, bool verifyNegativeControl)
    {
        Phase("terminal viewport geometry");
        var root = (FrameworkElement)host.Content;
        var client = CaptureClientRoot(root);
        var dpi = VisualTreeHelper.GetDpi(client);
        Phase($"terminal live coordinates: content={root.RenderSize}, HWND root={client.RenderSize}, content origin in HWND={root.TranslatePoint(new Point(), client)}");
        var terminal = body.Text.LastIndexOf(finalLine, StringComparison.Ordinal);
        Require(terminal > 0 && body.Text[(terminal + finalLine.Length)..].Trim('\r', '\n').Length == 0,
            "The synthetic marker is not the last nonblank line.");
        Require(body.ViewportHeight > 0 && Math.Abs(body.VerticalOffset -
            Math.Max(0, body.ExtentHeight - body.ViewportHeight)) <= 1 / dpi.DpiScaleY,
            "Production text did not reach the native terminal scroll offset.");

        var viewport = FindPresenter(body);
        Require(viewport is { ActualWidth: > 0, ActualHeight: > 0 }, "Production native scroll viewport not found.");
        var view = viewport!.TransformToAncestor(client).TransformBounds(
            new Rect(new Point(), viewport.RenderSize));
        var liveAnchor = Anchor(body, client, terminal);
        var end = Anchor(body, client, terminal + finalLine.Length - 1);
        Require(Math.Abs(liveAnchor.Y - end.Y) < 1 / dpi.DpiScaleY,
            "The marker wrapped; a one-line raster reference is not comparable.");
        Require(liveAnchor.Y >= view.Top - 1 / dpi.DpiScaleY && liveAnchor.Y < view.Bottom,
            "The native hit geometry cannot place the marker within its scroll viewport.");
        Phase($"terminal viewport settled: viewport={view}, anchor={liveAnchor}, dpi={dpi.PixelsPerInchY}");

        // Use the very same HistoryBodyBox factory and transparent HWND mode. The
        // isolated reference deliberately has no lower clip around the marker.
        // A chromatic foreground isolates the monochrome suffix from paper,
        // borders and header; color emoji is checked separately in native
        // selection, not claimed by this foreground-based raster.
        var factory = typeof(App).Assembly.GetType("HagoromoChan.Desktop.DesktopPanels", true)!;
        var reference = (TextBox)factory.GetMethod("HistoryBodyBox", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        reference.Text = finalLine;
        reference.Width = reference.MaxWidth = body.ActualWidth;
        var tint = new SolidColorBrush(Color.FromRgb(255, 0, 255));
        var old = body.Foreground;
        var referenceRoot = new Grid { ClipToBounds = true, Background = Brushes.Transparent };
        referenceRoot.Children.Add(reference);
        var p = root.PointToScreen(new Point());
        var window = new Window
        {
            AllowsTransparency = host.AllowsTransparency, Background = host.Background,
            Left = p.X / dpi.DpiScaleX, Top = p.Y / dpi.DpiScaleY,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = body.ActualWidth, Height = 100, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = host.WindowStyle, ResizeMode = host.ResizeMode,
            UseLayoutRounding = host.UseLayoutRounding, FontFamily = host.FontFamily, FontSize = host.FontSize,
            Content = referenceRoot
        };
        TextOptions.SetTextFormattingMode(reference, TextOptions.GetTextFormattingMode(body));
        TextOptions.SetTextRenderingMode(reference, TextOptions.GetTextRenderingMode(body));
        TextOptions.SetTextHintingMode(reference, TextOptions.GetTextHintingMode(body));
        try
        {
            Phase("terminal same-surface reference Show");
            window.Show();
            window.UpdateLayout();
            host.UpdateLayout();
            var referenceClient = CaptureClientRoot(referenceRoot);
            var referenceDpi = VisualTreeHelper.GetDpi(referenceClient);
            Phase($"terminal reference coordinates: content={referenceRoot.RenderSize}, HWND root={referenceClient.RenderSize}, content origin in HWND={referenceRoot.TranslatePoint(new Point(), referenceClient)}");
            Require(referenceDpi.DpiScaleX == dpi.DpiScaleX && referenceDpi.DpiScaleY == dpi.DpiScaleY,
                "Marker reference is on a different physical pixel scale.");
            var currentAnchor = Anchor(body, client, terminal);
            var currentView = viewport.TransformToAncestor(client).TransformBounds(new Rect(new Point(), viewport.RenderSize));
            Require(currentAnchor == liveAnchor && currentView == view,
                $"Live coordinate registration changed while opening the reference: [{liveAnchor}, {view}] -> [{currentAnchor}, {currentView}].");
            var referenceAnchor = Anchor(reference, referenceClient, 0);
            var referenceEnd = Anchor(reference, referenceClient, finalLine.Length - 1);
            Require(Math.Abs(referenceAnchor.Y - referenceEnd.Y) < 1 / dpi.DpiScaleY &&
                Math.Abs((end.X - liveAnchor.X) - (referenceEnd.X - referenceAnchor.X)) < 1 / dpi.DpiScaleX,
                "Production marker and reference do not share single-line metrics.");
            // Both captures share a caret-relative grid and retain the live
            // anchor's fractional device-pixel phase; only the reference is
            // translated fractionally. Actual uses its ancestor clips intact.
            const int leftGuard = 4, topGuard = 4;
            var x0 = (int)Math.Floor(liveAnchor.X * dpi.DpiScaleX) - leftGuard;
            var y0 = (int)Math.Floor(liveAnchor.Y * dpi.DpiScaleY) - topGuard;
            var w = (int)Math.Ceiling((end.X - liveAnchor.X + 30) * dpi.DpiScaleX) + leftGuard;
            var h = (int)Math.Ceiling(body.FontSize * 1.65 * dpi.DpiScaleY) + topGuard;
            Phase("terminal registered pixel comparison");
            var referenceX = x0 / dpi.DpiScaleX - liveAnchor.X + referenceAnchor.X;
            var referenceY = y0 / dpi.DpiScaleY - liveAnchor.Y + referenceAnchor.Y;
            Phase($"terminal capture origins in HWND: live=({x0 / dpi.DpiScaleX:F4},{y0 / dpi.DpiScaleY:F4}), reference=({referenceX:F4},{referenceY:F4}), pixels={w}x{h}");
            var expected = Capture(referenceClient, reference, tint, referenceX, referenceY, w, h, dpi);
            var actual = Capture(client, body, tint, x0 / dpi.DpiScaleX, y0 / dpi.DpiScaleY, w, h, dpi);
            // The original fixture has other lines above the marker. Only
            // ignore rows *above* its independently determined first ink;
            // never ignore a missing or extra bottom row.
            var first = Array.FindIndex(expected, pixel => pixel != 0);
            Require(first >= 0, "The aligned reference contains no marker pixels.");
            Array.Clear(actual, 0, first / w * w);
            var verdict = TerminalMask.Compare(expected, actual, w, out var expectedLast, out var actualLast);
            Require(expectedLast >= topGuard && expectedLast < h - 1,
                "The reference marker has no complete painted tail inside the comparison grid.");
            if (verifyNegativeControl)
            {
                // Perform an actual WPF ancestor clip and capture again, on
                // the same registered pixel grid. A synthetic boolean crop
                // alone would not exercise the brush/raster/clip pipeline.
                Phase("terminal reference: physical one-row ancestor clip");
                var cutoffInClient = new Point(referenceX, referenceY + expectedLast / dpi.DpiScaleY);
                var cutoffPoint = referenceClient.TranslatePoint(cutoffInClient, referenceRoot);
                var cutoff = cutoffPoint.Y;
                var roundTrip = referenceRoot.TranslatePoint(cutoffPoint, referenceClient);
                Require(Math.Abs(roundTrip.Y - cutoffInClient.Y) < 1e-6,
                    "The reference clip must round-trip to the original physical row.");
                Require(cutoff > 0 && cutoff < referenceRoot.ActualHeight,
                    "The reference clip boundary is outside its layout.");
                var originalClip = referenceRoot.Clip;
                byte[] clipped;
                try
                {
                    referenceRoot.Clip = new RectangleGeometry(new Rect(0, 0, referenceRoot.ActualWidth, cutoff));
                    clipped = Capture(referenceClient, reference, tint, referenceX, referenceY, w, h, dpi);
                }
                finally { referenceRoot.Clip = originalClip; }
                var clipVerdict = TerminalMask.Compare(expected, clipped, w, out _, out var clippedLast);
                var expectedOneRowClip = expected.ToArray();
                Array.Clear(expectedOneRowClip, expectedLast * w, w);
                Require(clipVerdict == TerminalMaskResult.BottomRowsMissing && clipped.AsSpan().SequenceEqual(expectedOneRowClip),
                    $"One-device-pixel ancestor clip was not isolated: ref={expectedLast}, clipped={clippedLast}, verdict={clipVerdict}.");
                Phase("terminal reference: physical one-row ancestor clip detected");
            }
            var lastVisible = view.Bottom * dpi.DpiScaleY - y0;
            Phase($"terminal mask={verdict}: ref={expectedLast}, live={actualLast}, viewport-bottom={lastVisible:F2}");
            var description = verdict == TerminalMaskResult.BottomRowsMissing
                ? (Math.Abs(lastVisible - (actualLast + 1)) <= 1
                    ? "lower viewport edge agrees with missing suffix"
                    : "matching prefix loses bottom rows away from reported viewport edge")
                : verdict == TerminalMaskResult.OtherMismatch
                    ? "different aligned shape; raster/environment mismatch or other drawing fault, not established clipping"
                    : "aligned mask matched";
            Require(verdict == TerminalMaskResult.Match,
                $"Terminal ink {verdict}: ref={expectedLast}, live={actualLast}, viewport-bottom={lastVisible:F2}; {description}.");
        }
        finally
        {
            window.Close();
            body.Foreground = old;
        }
        Phase("terminal registered pixels complete");
    }

    private static FrameworkElement CaptureClientRoot(FrameworkElement content)
    {
        // VisualBrush absolute viewboxes are in the painted visual's outer
        // space. Sampling the panel's inner coordinates omitted its bottom-
        // aligned offset when the test shrank the panel within its HWND.
        var client = PresentationSource.FromVisual(content)?.RootVisual as FrameworkElement
            ?? throw new InvalidOperationException("The capture content is not in a WPF HWND.");
        Require(VisualTreeHelper.GetParent(client) is null &&
            VisualTreeHelper.GetOffset(client) == new Vector() &&
            (VisualTreeHelper.GetTransform(client) is null or { Value.IsIdentity: true }),
            "The capture HWND root must use unshifted client coordinates.");
        return client;
    }

    private static Point Anchor(TextBox box, FrameworkElement captureRoot, int index)
    {
        Phase($"terminal native caret request {index}");
        var caret = box.GetRectFromCharacterIndex(index, trailingEdge: false);
        Require(!caret.IsEmpty && double.IsFinite(caret.X) && double.IsFinite(caret.Y),
            "Native TextBox did not return a terminal character position.");
        return box.TranslatePoint(caret.TopLeft, captureRoot);
    }

    private static ScrollContentPresenter? FindPresenter(DependencyObject control)
    {
        if (control is ScrollContentPresenter viewport) return viewport;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(control); i++)
            if (FindPresenter(VisualTreeHelper.GetChild(control, i)) is { } found)
                return found;
        return null;
    }

    private static byte[] Capture(FrameworkElement root, TextBox body, Brush tint,
        double x, double y, int w, int h, DpiScale dpi)
    {
        // Subtract a no-ink control drawn on this exact surface, preserving
        // clips, paper and the same subpixel grid in both capture pairs.
        body.Foreground = Brushes.Transparent;
        var paper = CapturePixels(root, x, y, w, h, dpi);
        body.Foreground = tint;
        var ink = CapturePixels(root, x, y, w, h, dpi);
        var mask = new byte[w * h];
        for (var i = 0; i < mask.Length; i++)
        {
            var j = i * 4;
            var blue = ink[j] - paper[j];
            var green = ink[j + 1] - paper[j + 1];
            var red = ink[j + 2] - paper[j + 2];
            mask[i] = Math.Min(blue, red) - green >= 42 ? (byte)1 : (byte)0;
        }
        return mask;
    }

    private static byte[] CapturePixels(FrameworkElement root, double x, double y, int w, int h, DpiScale dpi)
    {
        root.UpdateLayout();
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var sample = new Rect(x, y, w / dpi.DpiScaleX, h / dpi.DpiScaleY);
            var brush = new VisualBrush(root)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = sample,
                Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
            };
            dc.DrawRectangle(brush, null, new Rect(new Point(), sample.Size));
        }
        var bitmap = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
