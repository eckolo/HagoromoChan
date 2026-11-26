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
        }
        finally
        {
            Invoke("PrepareForShutdown");
            window.Close();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
