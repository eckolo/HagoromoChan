using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HagoromoChan.Contracts;
using HagoromoChan.Desktop;

internal static class Program
{
    // Render the app's real history cards with synthetic text; do not start the Relay,
    // load user data, or depend on the current user's SSP instance.
    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        var type = typeof(App).Assembly.GetType("HagoromoChan.Desktop.DesktopPanels", true)!;
        var owner = new Window();
        var panels = Activator.CreateInstance(type, owner, (Action)(() => { }),
            (Action)(() => { }), (Action<IReadOnlyList<string>>)(_ => { }), (Action)(() => { }))!;
        object? Invoke(string name, params object[] args) => type.GetMethod(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(panels, args);
        var window = (Window)type.GetProperty("Conversation")!.GetValue(panels)!;
        var root = (FrameworkElement)window.Content;
        window.Width = root.Width = 340;
        window.Height = 400;
        window.Show();
        try
        {
            var text = string.Concat(Enumerable.Repeat("折り返し確認ABC123日本語", 160)) + "最後です。";
            Invoke("UpdateHistory", new[] { new ChatMessage(1, "synthetic", "assistant", text, null,
                DateTimeOffset.UnixEpoch) }, Array.Empty<string>());
            Invoke("ToggleHistory");
            var overlaySide = type.Assembly.GetType("HagoromoChan.Desktop.OverlaySide", true)!;
            var scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
            var layout = type.Assembly.GetType("HagoromoChan.Desktop.BubbleLayout", true)!;
            var width = (double)layout.GetMethod("BodyWidth")!.Invoke(null, [root.Width, scale])!;
            var stack = (StackPanel)type.GetField("_historyStack", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panels)!;
            var anchor = type.GetMethod("SetAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            double? frameX = null;
            foreach (var side in new[] { "Right", "Left" })
            {
                var rebuilt = new System.Text.StringBuilder();
                for (var offset = 0; offset < text.Length;)
                {
                    anchor.Invoke(panels, ["synthetic", offset]);
                    Invoke("RenderHistory", width, 250.0, Enum.Parse(overlaySide, side), scale);
                    window.UpdateLayout();
                    Require(stack.Children.Count != 0, "History page is missing.");
                    var card = (DockPanel)stack.Children[0];
                    var frame = (Border)card.Children[1];
                    var body = (TextBox)((StackPanel)frame.Child).Children[1];
                    var x = frame.TranslatePoint(new Point(0, 0), root).X;
                    frameX ??= x;
                    Require(Math.Abs(x - frameX.Value) <= 1 / scale, "Text frame shifts when its ornament moves.");
                    Require(body.Text.Length > 0 && body.LineCount > 0, "Rendered page is empty.");
                    if (offset == 0)
                        Require(body.LineCount > 1, "Unbroken history text failed to wrap at the window edge.");
                    var last = body.GetRectFromCharacterIndex(body.Text.Length - 1, trailingEdge: true);
                    Require(!last.IsEmpty && last.Right <= body.ActualWidth + 1 / scale &&
                        last.Bottom <= body.ActualHeight + 1 / scale, "Last character falls outside its page.");
                    rebuilt.Append(body.Text);
                    offset += body.Text.Length;
                }
                Require(rebuilt.ToString() == text, "Paginated history lost or repeated text.");
            }
            Console.WriteLine("PASS M22 rendered WPF history wraps and pages without a horizontal frame shift");

            var topic = string.Join("\r\n", Enumerable.Range(1, 10).Select(i =>
                $"{i:D2} 合成の検証文です。日本語とABC123を混ぜた段落を折り返して表示します。次の行に続く文章を最後まで読めるか確認します。"))
                + "\r\n最終行を含む。";
            Require(topic.Length is >= 550 and <= 750, "Long topic fixture must stay close to 600 characters.");
            Invoke("UpdateHistory", new[]
            {
                new ChatMessage(1, "topic-user", "user", topic, null, DateTimeOffset.UnixEpoch),
                new ChatMessage(2, "topic-assistant", "assistant", topic, "topic-user", DateTimeOffset.UnixEpoch)
            }, Array.Empty<string>());
            foreach (var role in new[] { "user", "assistant" })
            foreach (var side in new[] { "Right", "Left", "Above", "Below" })
            {
                var start = 0;
                while (start < topic.Length)
                {
                    anchor.Invoke(panels, ["topic-" + role, start]);
                    Invoke("RenderHistory", width, 250.0, Enum.Parse(overlaySide, side), scale);
                    window.UpdateLayout();
                    Require(stack.Children.Count > 0, "Long topic history page disappeared.");
                    var card = (DockPanel)stack.Children[0];
                    Require(card.ActualHeight <= 250 + 1 / scale, "Native card exceeds its height budget.");
                    var frame = (Border)card.Children[1];
                    var body = (TextBox)((StackPanel)frame.Child).Children[1];
                    CheckBody(body, scale);
                    Require(body.Text.Length > 0 && topic.AsSpan(start).StartsWith(body.Text, StringComparison.Ordinal),
                        "History page breaks changed the message.");
                    start += body.Text.Length;
                }
                Require(start == topic.Length, "Not all history text is reachable.");
            }

            var editorHost = (FrameworkElement)type.GetField("_composerBody",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panels)!;
            var editor = (TextBox)type.GetProperty("MessageInput")!.GetValue(panels)!;
            var forced = type.GetField("_forcedDraftOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var draftWindow = new Window { Width = 540, Height = 790, Content = editorHost, ShowActivated = false };
            draftWindow.Show();
            try
            {
                var draftWidth = (double)layout.GetMethod("BodyWidth")!.Invoke(null, [500.0, scale])!;
                Invoke("SetDraftText", "短文");
                var oneLine = (double)Invoke("LayoutDraft", draftWidth, 650.0, scale)!;
                Invoke("SetDraftText", topic);
                forced.SetValue(panels, 0);
                var grown = (double)Invoke("LayoutDraft", draftWidth, 650.0, scale)!;
                draftWindow.UpdateLayout();
                Require(grown > oneLine && grown <= 650 && editor.Text == topic, "The draft did not grow to its needed height.");
                CheckBody(editor, scale);

                for (var start = 0; start < topic.Length;)
                {
                    forced.SetValue(panels, start);
                    var reduced = (double)Invoke("LayoutDraft", draftWidth, 125.0, scale)!;
                    draftWindow.UpdateLayout();
                    Require(reduced <= 125 + 1 / scale, "Draft height exceeded the compact budget.");
                    CheckBody(editor, scale);
                    Require(editor.Text.Length > 0 && topic.AsSpan(start).StartsWith(editor.Text, StringComparison.Ordinal),
                        "Draft page breaks changed the message.");
                    start += editor.Text.Length;
                }

                // Force native viewport pressure to exercise the escape path independently
                // of the normal (usually sufficient) detached measurement.
                editor.Text = topic;
                editor.Height = 44;
                draftWindow.UpdateLayout();
                var fixedWrapWidth = editor.ViewportWidth;
                Require(editor.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden &&
                    editor.ExtentHeight > editor.ViewportHeight, "Overflow fixture must need scrolling without a visible track.");

                // Reproduce the old width feedback on the actual styled TextBox.
                editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                draftWindow.UpdateLayout();
                Require(fixedWrapWidth - editor.ViewportWidth >= 1 / scale,
                    "The former auto scrollbar did not reproduce a narrower native wrapping viewport.");
                editor.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                draftWindow.UpdateLayout();
                Require(Math.Abs(editor.ViewportWidth - fixedWrapWidth) <= 1 / scale,
                    "Fallback scrolling changed the measured wrapping width.");
                editor.ScrollToEnd();
                draftWindow.UpdateLayout();
                Require(editor.VerticalOffset > 0, "The bounded editor cannot scroll to the bottom on native overflow.");
                CheckBody(editor, scale);
            }
            finally { draftWindow.Content = null; draftWindow.Close(); }
            Console.WriteLine("PASS long WPF sent/received history and editable draft have bounded, reachable final lines");

            // Exercise the real positioning entry point, not solely a manually
            // arranged text box. Use an inert owner at the work area's edge.
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Width = 132;
            owner.Height = 140;
            owner.ShowActivated = false;
            owner.Show();
            var desktop = type.Assembly.GetType("HagoromoChan.Desktop.NativeDesktop", true)!;
            var area = desktop.GetMethod("WorkArea", [typeof(Window)])!.Invoke(null, [owner])!;
            int Px(object rect, string name) => (int)rect.GetType().GetProperty(name)!.GetValue(rect)!;
            var edge = Activator.CreateInstance(area.GetType(), [
                Px(area, "Right") - 136, Px(area, "Bottom") - 144, 132, 140])!;
            desktop.GetMethod("SetBounds")!.Invoke(null, [owner, edge]);
            Invoke("Attach");
            Invoke("ShowMessage", "topic-user");
            window.UpdateLayout();
            var bounds = desktop.GetMethod("Bounds")!.Invoke(null, [window])!;
            Require(Px(bounds, "X") >= Px(area, "X") && Px(bounds, "Y") >= Px(area, "Y") &&
                Px(bounds, "Right") <= Px(area, "Right") && Px(bounds, "Bottom") <= Px(area, "Bottom"),
                "The production HWND escaped the monitor's work area.");
            var (nativeWidth, nativeHeight) = ((int, int))desktop.GetMethod("ClientSize")!.Invoke(null, [window])!;
            var actualDpi = VisualTreeHelper.GetDpi(root).DpiScaleX;
            Require(root.ClipToBounds && root.MaxHeight * actualDpi <= nativeHeight + 1
                && root.Width * actualDpi <= nativeWidth + 1, "The actual client budget is exceeded by the content host.");
            Require(stack.Children.Count > 0, "Current sent message did not render via the production placement path.");
            void InsideClient(FrameworkElement frame)
            {
                var origin = frame.TranslatePoint(new Point(0, 0), window);
                Require(origin.Y >= -1 / actualDpi &&
                    (origin.Y + frame.ActualHeight) * actualDpi <= nativeHeight + 1,
                    "A production frame protrudes past the native client's bottom.");
            }
            foreach (FrameworkElement card in stack.Children)
                InsideClient(card);

            // The isolated draft host was detached before it closed.
            Invoke("SetDraftText", topic);
            Invoke("OpenBoth");
            window.UpdateLayout();
            var composer = (FrameworkElement?)type.GetField("_composerBubble",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panels);
            Require(composer?.IsVisible == true, "The live composer did not fit the production client.");
            InsideClient(composer!);
            CheckBody(editor, actualDpi);
            Console.WriteLine("PASS placement path keeps the native window and rendered frames in the work area");
        }
        finally
        {
            Invoke("PrepareForShutdown");
            window.Close();
            if (owner.IsVisible)
                owner.Close();
        }
    }

    private static void CheckBody(TextBox box, double scale)
    {
        Require(box.Text.Length > 0 && box.LineCount > 0, "Body page is empty.");
        if (box.ExtentHeight > box.ViewportHeight + 1 / scale)
        {
            box.ScrollToEnd();
            box.UpdateLayout();
        }
        var last = box.GetRectFromCharacterIndex(box.Text.Length - 1, trailingEdge: true);
        // A page may end at a line break; in that case WPF puts the caret on
        // the next line, which the page budget includes.
        Require(!last.IsEmpty && last.Right <= box.ActualWidth + 1 / scale
            && last.Bottom <= box.ActualHeight + 1 / scale, "Last character is cut off in the native viewport.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
