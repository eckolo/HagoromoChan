using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HagoromoChan.Contracts;
using HagoromoChan.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        var type = typeof(App).Assembly.GetType("HagoromoChan.Desktop.DesktopPanels", true)!;
        var owner = new Window { WindowStartupLocation = WindowStartupLocation.Manual, Width = 132, Height = 140, ShowActivated = false };
        var panels = Activator.CreateInstance(type, owner, (Action)(() => { }),
            (Action)(() => { }), (Action<IReadOnlyList<string>>)(_ => { }), (Action)(() => { }))!;
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object? Invoke(string name, params object[] args) => type.GetMethod(name, Flags)!.Invoke(panels, args);
        var window = (Window)type.GetProperty("Conversation")!.GetValue(panels)!;
        var root = (FrameworkElement)window.Content;
        var stack = (StackPanel)type.GetField("_historyStack", Flags)!.GetValue(panels)!;
        var editor = (TextBox)type.GetProperty("MessageInput")!.GetValue(panels)!;
        var desktop = type.Assembly.GetType("HagoromoChan.Desktop.NativeDesktop", true)!;
        var sideType = type.Assembly.GetType("HagoromoChan.Desktop.OverlaySide", true)!;
        var layout = type.Assembly.GetType("HagoromoChan.Desktop.BubbleLayout", true)!;
        owner.Show();
        try
        {
            var area = desktop.GetMethod("WorkArea", [typeof(Window)])!.Invoke(null, [owner])!;
            int Px(object rect, string name) => (int)rect.GetType().GetProperty(name)!.GetValue(rect)!;
            var edge = Activator.CreateInstance(area.GetType(), [
                Px(area, "Right") - 136, Px(area, "Bottom") - 144, 132, 140])!;
            desktop.GetMethod("SetBounds")!.Invoke(null, [owner, edge]);
            Invoke("Attach");
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

            (DockPanel Card, Border Frame, Decorator View, TextBox Body) Active()
            {
                Require(stack.Children.Count > 0, "Production history card disappeared.");
                var card = (DockPanel)stack.Children[0];
                var frame = (Border)card.Children[1];
                var view = (Decorator)((StackPanel)frame.Child).Children[1];
                return (card, frame, view, (TextBox)view.Child);
            }
            void ExerciseNativeTextHit(Decorator viewport, TextBox box)
            {
                // The production-shaped, attached read-only TextBox must still
                // support point-to-text and selection. UI Automation invokes
                // native hit-testing without moving the operator's pointer.
                var peer = new TextBoxAutomationPeer(box);
                var provider = peer.GetPattern(PatternInterface.Text) as ITextProvider;
                Require(provider is not null, "The retained history body lost its native text provider.");
                var pixel = viewport.PointToScreen(new Point(viewport.ActualWidth / 4, viewport.ActualHeight / 3));
                var hit = provider!.RangeFromPoint(pixel);
                Require(hit is not null, "The native history TextBox cannot hit-test its live attached viewport.");
                hit!.ExpandToEnclosingUnit(TextUnit.Character);
                hit.Select();
                Require(box.SelectionLength > 0, "Point-to-text did not select content in the retained TextBox.");
            }
            double ViewTop(Decorator view) => (double)view.GetType().GetProperty("ViewTop", Flags)!.GetValue(view)!;
            bool Known(Decorator view) => (bool)view.GetType().GetProperty("GeometryKnown", Flags)!.GetValue(view)!;
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
                Invoke("ShowMessage", id);
                window.UpdateLayout();
                var bounds = desktop.GetMethod("Bounds")!.Invoke(null, [window])!;
                Require(Px(bounds, "X") >= Px(area, "X") && Px(bounds, "Y") >= Px(area, "Y") &&
                    Px(bounds, "Right") <= Px(area, "Right") && Px(bounds, "Bottom") <= Px(area, "Bottom"),
                    "The native HWND escaped the work area.");
                var (card, _, view, body) = Active();
                InsideClient(card);
                Require(Known(view) && body.Text == topic, "Production 600-character fixture uses unverified fallback.");
                if (body.ActualHeight <= view.ActualHeight)
                    CheckTailInk(view, body, ViewTop(view), verifyNegativeControl: id == "topic-user");
                ExerciseNativeTextHit(view, body);
            }

            Invoke("ShowMessage", "long");
            window.UpdateLayout();
            var (_, _, beforeArrows, arrowBody) = Active();
            var topBefore = ViewTop(beforeArrows);
            if (arrowBody.ActualHeight > beforeArrows.ActualHeight)
            {
                Invoke("MoveHistory", 1);
                window.UpdateLayout();
                var (_, _, afterArrows, sameArrowBody) = Active();
                Require(ReferenceEquals(arrowBody, sameArrowBody) && ViewTop(afterArrows) > topBefore,
                    "Production down arrow skipped a whole page of the same message.");
                Invoke("MoveHistory", -1);
                window.UpdateLayout();
                var (_, _, returned, _) = Active();
                Require(ViewTop(returned) == topBefore, "Production up/down was not reversible.");
            }
            var gpuScale = VisualTreeHelper.GetDpi(root).DpiScaleX;
            var width = (double)layout.GetMethod("BodyWidth")!.Invoke(null, [root.ActualWidth, gpuScale])!;
            double? x = null;
            foreach (var side in new[] { "Right", "Left" })
            {
                Invoke("SetAnchor", "long", 0);
                Invoke("RenderHistory", width, 175.0, Enum.Parse(sideType, side), gpuScale);
                window.UpdateLayout();
                var (_, frame, page, body) = Active();
                var origin = frame.TranslatePoint(new Point(0, 0), root).X;
                x ??= origin;
                Require(Math.Abs(origin - x.Value) <= 1 / gpuScale, "M22 frame moves when only the tail switches sides.");
                Require(Known(page) && body.Text == longText && body.LineCount > 1, "CRLF/emoji text lost or unverified.");
            }
            for (var i = 0; i < 2; i++)
            {
                Invoke("SetAnchor", "long", 0);
                Invoke("RenderHistory", width, 175.0, Enum.Parse(sideType, "Left"), gpuScale);
                window.UpdateLayout();
                var (_, _, first, body) = Active();
                Require(ViewTop(first) == 0, "The first page did not start at the top.");
                body.Select(0, 3);
                // Request the terminal physical page; no character/line replay.
                // The same TextBox still contains the original trailing CRLF.
                Invoke("SetAnchor", "long", int.MaxValue);
                Invoke("RenderHistory", width, 175.0, Enum.Parse(sideType, "Left"), gpuScale);
                window.UpdateLayout();
                var (card, _, last, sameBody) = Active();
                InsideClient(card);
                Require(ReferenceEquals(first, last) && ReferenceEquals(body, sameBody) &&
                    sameBody.SelectionLength == 3 && ViewTop(last) > 0 && Known(last),
                    "Paging recreated the native body, altered selection, or missed its tail.");
                sameBody.Select(0, 0);
                CheckTailInk(last, sameBody, ViewTop(last), verifyNegativeControl: false);
                ExerciseNativeTextHit(last, sameBody);
            }

            Invoke("SetDraftText", longText);
            Invoke("OpenBoth");
            window.UpdateLayout();
            var composer = (FrameworkElement?)type.GetField("_composerBubble", Flags)!.GetValue(panels);
            Require(composer?.IsVisible == true, "Live composer missing.");
            InsideClient(composer!);
            Require(editor.Text == longText && editor.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden,
                "Composer no longer contains the full original text or gained a width-changing scrollbar.");
            editor.CaretIndex = editor.Text.Length;
            editor.ScrollToEnd();
            window.UpdateLayout();
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

    private static void CheckTailInk(Decorator view, TextBox body, double top, bool verifyNegativeControl)
    {
        var scale = VisualTreeHelper.GetDpi(view).DpiScaleY;
        // Pixel oracle: derive the last non-transparent row from the full native
        // body and compare with the actual clipped parent viewport. This does
        // not reuse the page planner's line bounds or caret edge measurement.
        int LastAlpha(Visual visual, double width, double height)
        {
            var w = Math.Max(1, (int)Math.Ceiling(width * scale));
            var h = Math.Max(1, (int)Math.Ceiling(height * scale));
            var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var bytes = new byte[w * h * 4];
            bitmap.CopyPixels(bytes, w * 4, 0);
            for (var y = h - 1; y >= 0; y--)
                for (var x = 0; x < w; x++)
                    if (bytes[(y * w + x) * 4 + 3] >= 32) return y;
            return -1;
        }

        int expected = LastAlpha(body, body.ActualWidth, body.ActualHeight) - (int)Math.Round(top * scale);
        int actual = LastAlpha(view, view.ActualWidth, view.ActualHeight);
        Require(expected >= 0 && actual == expected,
            $"Last painted glyph row is missing: expected device-pixel row {expected}, got {actual}.");
        if (!verifyNegativeControl) return;
        var prior = view.Clip;
        try
        {
            // Hide exactly the final ink-bearing device row. The oracle must
            // see the failure even when the caret and line count still exist.
            view.Clip = new RectangleGeometry(new Rect(0, 0, view.ActualWidth, expected / scale));
            view.UpdateLayout();
            Require(LastAlpha(view, view.ActualWidth, view.ActualHeight) < expected,
                "One-device-pixel clipping negative control was not detected.");
        }
        finally { view.Clip = prior; view.UpdateLayout(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
