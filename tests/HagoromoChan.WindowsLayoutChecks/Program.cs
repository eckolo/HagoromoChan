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
                editor.ScrollToEnd();
                draftWindow.UpdateLayout();
                Require(editor.ExtentHeight > editor.ViewportHeight && editor.VerticalOffset > 0,
                    "The bounded editor cannot scroll to the bottom on native overflow.");
                CheckBody(editor, scale);
            }
            finally { draftWindow.Close(); }
            Console.WriteLine("PASS long WPF sent/received history and editable draft have bounded, reachable final lines");
        }
        finally
        {
            Invoke("PrepareForShutdown");
            window.Close();
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
