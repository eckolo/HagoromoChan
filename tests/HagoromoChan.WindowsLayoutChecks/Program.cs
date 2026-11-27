using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HagoromoChan.LayoutChecks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HagoromoChan.Contracts;
using HagoromoChan.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["long-all"] or ["layout-all"])
        {
            Environment.ExitCode = LongDiagnostics.Run(layout: args[0] == "layout-all");
            return;
        }
        var mode = args.Length == 0 ? "full" : args.Length == 1 ? args[0] : null;
        if (mode is not ("full" or "long-combined" or "long-selection-only" or "long-scroll-only"
            or "long-selection-then-jump" or "long-selected-arrow" or "long-selected-wheel"
            or "layout-short" or "layout-long" or "layout-constrained" or "layout-navigation" or "layout-composer"))
            throw new ArgumentException("Use no arguments or exactly one M35 layout diagnostic mode.");
        var longDiagnostic = mode.StartsWith("long-", StringComparison.Ordinal);
        var wpf = typeof(TextBox).Assembly;
        Phase($"case={mode}, runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, "
            + $"WPF={wpf.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
        // A real Desktop App schedules OnStartup on the dispatcher. The
        // Loaded retry below pumps that dispatcher; production startup here
        // would open the user's data directory and take its instance lock.
        // Load the same styles without ever constructing a production App.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/HagoromoChan.Desktop;component/DesktopResources.xaml", UriKind.Relative)
        });
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
            Invoke("UpdateHistory", new[]
            {
                new ChatMessage(1, "topic-user", "user", topic, null, DateTimeOffset.UnixEpoch),
                new ChatMessage(2, "topic-assistant", "assistant", topic, "topic-user", DateTimeOffset.UnixEpoch),
                new ChatMessage(3, "long", "assistant", longText, "topic-user", DateTimeOffset.UnixEpoch)
            }, Array.Empty<string>());

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

            if (mode is "full" or "layout-short" || longDiagnostic)
            {
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
                    body.ScrollToEnd();
                    Layout(id + " native end");
                    CheckTailInk(window, body, "gypqç", verifyNegativeControl: id == "topic-user");
                    ExerciseNativeTextHit(body);
                }
            }

            void Passed() => Console.WriteLine($"PASS M35 {mode}");
            if (mode == "layout-short") { Passed(); return; }
            if (mode == "layout-composer") { CheckComposer(); Passed(); return; }

            Phase("ShowMessage long");
            Invoke("ShowMessage", "long");
            Layout("long initial");
            var (_, _, arrowBody) = Active();
            Require(arrowBody.Text == longText && arrowBody.ExtentHeight > arrowBody.ViewportHeight,
                "Long fixture did not require a real native scroll viewport.");
            var scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
            if (mode is "full" or "layout-long" || longDiagnostic)
            {
                CheckHistoryArrowsAndFrame();
            }

            void CheckHistoryArrowsAndFrame()
            {
                var topBefore = arrowBody.VerticalOffset;
                Invoke("MoveHistory", 1);
                Layout("long down");
                Require(ReferenceEquals(Active().Body, arrowBody) && arrowBody.VerticalOffset > topBefore,
                    "Production down arrow advanced the wrong message instead of scrolling its native body.");
                Invoke("MoveHistory", -1);
                Layout("long up");
                Require(Math.Abs(arrowBody.VerticalOffset - topBefore) < .01, "Native up/down was not reversible.");

                var width = (double)layout.GetMethod("BodyWidth")!.Invoke(null, [root.ActualWidth, scale])!;
                double? x = null;
                foreach (var side in new[] { "Right", "Left" })
                {
                    Invoke("RenderHistory", width, (double)root.ActualHeight, Enum.Parse(sideType, side), scale);
                    Layout("M22 side " + side);
                    var (_, frame, body) = Active();
                    var origin = frame.TranslatePoint(new Point(0, 0), root).X;
                    x ??= origin;
                    Require(Math.Abs(origin - x.Value) <= 1 / scale, "M22 frame moves when only the tail switches sides.");
                    Require(ReferenceEquals(body, arrowBody), "M22 frame change recreated the retained native body.");
                }
            }
            if (longDiagnostic)
            {
                // Each case must be started in a fresh process. A FailFast is
                // uncatchable and cannot be a control for another case.
                void State(string phase)
                {
                    Phase($"{phase}: sel={arrowBody.SelectionStart}/{arrowBody.SelectionLength}, "
                        + $"active={arrowBody.IsSelectionActive}, focus={arrowBody.IsKeyboardFocusWithin}, "
                        + $"arranged={arrowBody.IsArrangeValid}, width={arrowBody.ActualWidth:F4}, "
                        + $"offset={arrowBody.VerticalOffset:F4}, extent={arrowBody.ExtentHeight:F4}, "
                        + $"viewport={arrowBody.ViewportHeight:F4}");
                    Require(ReferenceEquals(Active().Body, arrowBody) && arrowBody.Text == longText,
                        "The diagnostic must keep the same full document and live native body.");
                }

                State("diagnostic starting control");
                if (mode is "long-selected-arrow" or "long-selected-wheel")
                {
                    ExerciseNativeTextHit(arrowBody);
                    Layout("diagnostic native selection settled");
                    State("diagnostic native selection");
                    var selectionStart = arrowBody.SelectionStart;
                    var selectionLength = arrowBody.SelectionLength;
                    Require(selectionLength > 0, "No retained native selection to exercise.");
                    var newer = (RepeatButton)type.GetField("_newer", Flags)!.GetValue(panels)!;
                    var guard = 0;
                    while (arrowBody.ExtentHeight - arrowBody.ViewportHeight - arrowBody.VerticalOffset > .01)
                    {
                        Require(++guard <= 256, "Production navigation did not reach the terminal extent.");
                        var offset = arrowBody.VerticalOffset;
                        if (mode == "long-selected-arrow")
                        {
                            Require(newer.IsEnabled, "Production arrow is disabled before the terminal extent.");
                            Phase($"diagnostic production arrow {guard}");
                            newer.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, newer));
                        }
                        else
                        {
                            Phase($"diagnostic production wheel {guard}; route={(arrowBody.IsMouseOver ? "preview hovered body" : "preview fallback to current history")}; synthetic event, no physical mouse input");
                            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                            {
                                RoutedEvent = Mouse.PreviewMouseWheelEvent,
                                Source = arrowBody
                            };
                            arrowBody.RaiseEvent(wheel);
                            Require(wheel.Handled, "Production wheel handler did not consume the routed event.");
                        }
                        Layout($"diagnostic {mode} {guard}");
                        State("diagnostic after production navigation");
                        Require(arrowBody.VerticalOffset > offset && arrowBody.SelectionStart == selectionStart &&
                            arrowBody.SelectionLength == selectionLength, "Production navigation lost progress or selection.");
                    }
                    Require(guard > 0, "The production navigation control did not run.");
                }
                else
                {
                    if (mode != "long-scroll-only")
                    {
                        Phase("diagnostic direct Select(0,3)");
                        arrowBody.Select(0, 3);
                        if (mode is "long-selection-only" or "long-selection-then-jump")
                        {
                            Layout("diagnostic selection only");
                            State("diagnostic after direct settled selection");
                            Require(arrowBody.SelectionLength == 3, "Direct selection was not retained.");
                        }
                    }
                    if (mode != "long-selection-only")
                    {
                        Phase("diagnostic direct ScrollToEnd");
                        arrowBody.ScrollToEnd();
                        Layout("diagnostic direct terminal jump");
                    }
                }
                State("diagnostic complete");
                if (mode != "long-selection-only")
                    Require(arrowBody.VerticalOffset > 0 && Math.Abs(arrowBody.VerticalOffset -
                        (arrowBody.ExtentHeight - arrowBody.ViewportHeight)) <= 1 / scale,
                        "The terminal diagnostics must reach the actual native scroll extent.");
                Console.WriteLine($"PASS M35 {mode}");
                return;
            }

            var retainedBody = arrowBody;
            if (mode is "full" or "layout-long")
            {
                CheckLongTerminal();
                if (mode == "layout-long") { Passed(); return; }
            }
            if (mode is "full" or "layout-constrained")
            {
                CheckConstrainedHistory();
                if (mode == "layout-constrained") { Passed(); return; }
            }
            if (mode is "full" or "layout-navigation")
            {
                CheckHistoryNavigation();
                if (mode == "layout-navigation") { Passed(); return; }
            }
            CheckComposer();

            void CheckLongTerminal()
            {
                Phase("long terminal: direct Select(0,3) and ScrollToEnd in the same dispatcher turn");
                arrowBody.Select(0, 3);
                arrowBody.ScrollToEnd();
                Layout("long terminal");
                var (longCard, _, currentBody) = Active();
                InsideClient(longCard);
                Require(ReferenceEquals(retainedBody, currentBody) && retainedBody.SelectionLength == 3 &&
                    retainedBody.Text == longText && retainedBody.VerticalOffset > 0 &&
                    Math.Abs(retainedBody.VerticalOffset - (retainedBody.ExtentHeight - retainedBody.ViewportHeight)) <= 1 / scale,
                    "Native terminal scrolling lost the original Unicode text, selection or final extent.");
                retainedBody.Select(0, 0);
                CheckTailInk(window, retainedBody, "末尾gypq語", verifyNegativeControl: true);
                ExerciseNativeTextHit(retainedBody);
            }

            void CheckConstrainedHistory()
            {
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
            }

            void CheckHistoryNavigation()
            {
                retainedBody.ScrollToHome();
                Layout("history older initial cursor");
                Invoke("MoveHistory", -1);
                Layout("history older document");
                var previous = Active().Body;
                Require(previous.Text == topic &&
                    Math.Abs(previous.VerticalOffset - Math.Max(0, previous.ExtentHeight - previous.ViewportHeight)) <= 1 / scale,
                    "Crossing into the older history item did not land at its native end.");
                Invoke("MoveHistory", 1);
                Layout("history newer document");
                Require(Active().Body.Text == longText && Active().Body.VerticalOffset <= 1 / scale,
                    "Crossing into the newer history item did not land at its native beginning.");
            }

            void CheckComposer()
            {
                Phase("SetDraftText and OpenBoth");
                Invoke("SetDraftText", longText);
                Invoke("OpenBoth");
                Layout("draft initial");
                var composer = (FrameworkElement?)type.GetField("_composerBubble", Flags)!.GetValue(panels);
                Require(composer?.IsVisible == true, "Live composer missing.");
                InsideClient(composer!);
                Require(editor.Text == longText && editor.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden,
                    "Composer no longer contains the full original text or gained a width-changing scrollbar.");
                editor.CaretIndex = editor.Text.Length;
                editor.ScrollToEnd();
                Layout("draft terminal");
                var end = editor.GetRectFromCharacterIndex(editor.Text.Length, trailingEdge: true);
                Require(editor.ExtentHeight > editor.ViewportHeight && editor.VerticalOffset > 0 &&
                    !end.IsEmpty && end.Bottom <= editor.ActualHeight,
                    "Native composer could not reach the final caret/line in its bounded viewport.");
            }
            Console.WriteLine("PASS M35 production HWND, whole-ink graphics, M22 frame, retained Unicode text/selection and draft caret");
        }
        finally
        {
            Invoke("PrepareForShutdown");
            if (window.IsLoaded) window.Close();
            if (owner.IsVisible) owner.Close();
        }
    }

    private static void Phase(string phase)
    {
        Console.WriteLine("M35 STAGE: " + phase);
        Console.Out.Flush();
    }

    private static void CheckTailInk(Window host, TextBox body, string finalLine, bool verifyNegativeControl)
    {
        Phase("terminal viewport geometry");
        var root = (FrameworkElement)host.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var terminal = body.Text.LastIndexOf(finalLine, StringComparison.Ordinal);
        Require(terminal > 0 && body.Text[(terminal + finalLine.Length)..].Trim('\r', '\n').Length == 0,
            "The synthetic marker is not the last nonblank line.");
        Require(body.ViewportHeight > 0 && Math.Abs(body.VerticalOffset -
            Math.Max(0, body.ExtentHeight - body.ViewportHeight)) <= 1 / dpi.DpiScaleY,
            "Production text did not reach the native terminal scroll offset.");

        var viewport = FindPresenter(body);
        Require(viewport is { ActualWidth: > 0, ActualHeight: > 0 }, "Production native scroll viewport not found.");
        var view = viewport!.TransformToAncestor(root).TransformBounds(
            new Rect(new Point(), viewport.RenderSize));
        var liveAnchor = Anchor(body, root, terminal);
        var end = Anchor(body, root, terminal + finalLine.Length - 1);
        Require(Math.Abs(liveAnchor.Y - end.Y) < 1 / dpi.DpiScaleY,
            "The marker wrapped; a one-line raster reference is not comparable.");
        Require(liveAnchor.Y >= view.Top - 1 / dpi.DpiScaleY && liveAnchor.Y < view.Bottom,
            "The native hit geometry cannot place the marker within its scroll viewport.");
        Phase($"terminal viewport settled: viewport={view}, anchor={liveAnchor}, dpi={dpi.PixelsPerInchY}");

        // Use the very same BodyBox constructor and transparent HWND mode. The
        // isolated reference deliberately has no lower clip around the marker.
        // A chromatic foreground isolates the monochrome suffix from paper,
        // borders and header; color emoji is checked separately in native
        // selection, not claimed by this foreground-based raster.
        var factory = typeof(App).Assembly.GetType("HagoromoChan.Desktop.DesktopPanels", true)!;
        var reference = (TextBox)factory.GetMethod("BodyBox", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        reference.IsReadOnly = true;
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
            var referenceDpi = VisualTreeHelper.GetDpi(referenceRoot);
            Require(referenceDpi.DpiScaleX == dpi.DpiScaleX && referenceDpi.DpiScaleY == dpi.DpiScaleY,
                "Marker reference is on a different physical pixel scale.");
            var referenceAnchor = Anchor(reference, referenceRoot, 0);
            var referenceEnd = Anchor(reference, referenceRoot, finalLine.Length - 1);
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
            var expected = Capture(referenceRoot, reference, tint, referenceX, referenceY, w, h, dpi);
            var actual = Capture(root, body, tint, x0 / dpi.DpiScaleX, y0 / dpi.DpiScaleY, w, h, dpi);
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
                var cutoff = referenceY + expectedLast / dpi.DpiScaleY;
                Require(cutoff > 0 && cutoff < referenceRoot.ActualHeight,
                    "The reference clip boundary is outside its layout.");
                var originalClip = referenceRoot.Clip;
                byte[] clipped;
                try
                {
                    referenceRoot.Clip = new RectangleGeometry(new Rect(0, 0, referenceRoot.ActualWidth, cutoff));
                    clipped = Capture(referenceRoot, reference, tint, referenceX, referenceY, w, h, dpi);
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
