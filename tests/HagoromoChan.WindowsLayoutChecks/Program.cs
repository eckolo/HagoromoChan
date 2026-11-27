using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
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
                body.ScrollToEnd();
                Layout(id + " native end");
                CheckTailInk(body, "最終行：語😀 gypqç", verifyNegativeControl: id == "topic-user");
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
                Layout("M22 side " + side);
                var (_, frame, body) = Active();
                var origin = frame.TranslatePoint(new Point(0, 0), root).X;
                x ??= origin;
                Require(Math.Abs(origin - x.Value) <= 1 / scale, "M22 frame moves when only the tail switches sides.");
                Require(ReferenceEquals(body, arrowBody), "M22 frame change recreated the retained native body.");
            }
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
            CheckTailInk(retainedBody, "末尾gypq語", verifyNegativeControl: true);
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
            CheckTailInk(retainedBody, "末尾gypq語", verifyNegativeControl: false);
            root.GetType().GetMethod("SetClientBudget", Flags)!.Invoke(root, [root.ActualWidth, room]);
            Layout("restore native client");

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

    private static void CheckTailInk(TextBox body, string finalLine, bool verifyNegativeControl)
    {
        Phase("native ink capture");
        // Reference: a separate ordinarily laid-out native TextBox holding only
        // the final explicit line, not another measure of the production body.
        // Compare the last uninterrupted glyph row band (including descenders).
        var reference = new TextBox
        {
            Text = finalLine, FontFamily = body.FontFamily, FontSize = body.FontSize,
            FontWeight = body.FontWeight, FontStretch = body.FontStretch, FontStyle = body.FontStyle,
            Foreground = body.Foreground, Background = Brushes.Transparent, UseLayoutRounding = true,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), IsReadOnly = true,
            Width = body.ActualWidth, TextWrapping = body.TextWrapping,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var p = body.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(body);
        var window = new Window
        {
            Left = p.X / dpi.DpiScaleX, Top = p.Y / dpi.DpiScaleY,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = body.ActualWidth, Height = 70, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = reference
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Require(VisualTreeHelper.GetDpi(reference).DpiScaleY == dpi.DpiScaleY,
                "Reference marker is not on the production DPI.");
            int TailBand(TextBox text, int? pixelHeight = null)
            {
                var w = Math.Max(1, (int)Math.Ceiling(text.ActualWidth * dpi.DpiScaleX));
                var h = pixelHeight ?? Math.Max(1, (int)Math.Ceiling(text.ActualHeight * dpi.DpiScaleY));
                var bitmap = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(text);
                return LastBand(bitmap).Rows;
            }
            static (int Bottom, int Rows) LastBand(BitmapSource bitmap)
            {
                var w = bitmap.PixelWidth;
                var h = bitmap.PixelHeight;
                var bytes = new byte[w * h * 4];
                bitmap.CopyPixels(bytes, w * 4, 0);
                int bottom = -1, rows = 0;
                for (var y = h - 1; y >= 0; y--)
                {
                    var ink = false;
                    for (var ix = 0; ix < w; ix++)
                        if (bytes[(y * w + ix) * 4 + 3] >= 32) { ink = true; break; }
                    if (!ink && bottom >= 0) break;
                    if (ink) { if (bottom < 0) bottom = y; rows++; }
                }
                return (bottom, rows);
            }
            var expected = TailBand(reference);
            var wb = (int)Math.Ceiling(body.ActualWidth * dpi.DpiScaleX);
            var hb = (int)Math.Ceiling(body.ActualHeight * dpi.DpiScaleY);
            var actual = new RenderTargetBitmap(wb, hb, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            actual.Render(body);
            var (bottom, rows) = LastBand(actual);
            Require(expected >= 4 && rows == expected,
                $"Final native glyph band loses pixels: reference {expected}, shown {rows}.");
            if (verifyNegativeControl)
            {
                var cropped = new CroppedBitmap(actual, new Int32Rect(0, 0, wb, bottom));
                Require(LastBand(cropped).Rows != expected, "One-device-pixel crop was not detected.");
            }
        }
        finally { window.Close(); }
        Phase("native ink capture completed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
