using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HagoromoChan.Desktop;

// Only the mascot window exists visibly during idle. Each panel has its own compact
// layered HWND, so the gaps between panels are not covered by an input surface.
internal sealed class DesktopPanels
{
    private readonly Window _owner;
    private readonly HashSet<Window> _requested = [];
    private readonly HashSet<Window> _hooked = [];
    private bool _attached;
    private bool _closing;
    private bool _arranging;
    private readonly Polygon _tail = new() { Fill = Brush(0xFFF7E5), Stroke = Brush(0xD4A55D), StrokeThickness = 1 };
    private readonly DockPanel _bubbleRoot = new();
    private readonly TextBlock _replyTitle = Label("羽衣ちゃん");
    private readonly TextBlock _replyCount = Label("");
    private readonly TextBox _replyBody = ReadOnlyText(Brush(0x2E2530));
    private readonly Button _previous = Button("前へ");
    private readonly Button _next = Button("次へ");
    private readonly Button _newest = Button("新着");
    private readonly StackPanel _statusContent = new();
    private readonly TextBlock _composerHint = Label("Enter: 改行　Ctrl+Enter: 送信", 11);
    private readonly Window[] _windows;
    private readonly Dictionary<(string Text, double Width, double FontSize), double> _measurements = [];

    public event Action? VisibilityChanged;

    public Window Hover { get; }
    public Window Bubble { get; }
    public Window Composer { get; }
    public Window History { get; }
    public Window Status { get; }

    public TextBox MessageInput { get; } = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 32768,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        BorderThickness = new Thickness(0), Padding = new Thickness(5),
        Background = Brush(0x17212F), Foreground = Brush(0xF0F3F7), CaretBrush = Brush(0xF0F3F7)
    };
    public Button SendButton { get; } = Button("送信");
    public TextBlock SendFeedbackText { get; } = Label("", 12);
    public ItemsControl HistoryItems { get; } = new();
    public ScrollViewer HistoryScroll { get; } = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    public TextBlock EmptyHistoryText { get; } = Label("会話を読み込んでいます…");
    public Button NewMessagesButton { get; } = Button("新しいメッセージへ");
    public TextBlock ConnectionText { get; } = Label("接続を確認しています", 12);
    public Ellipse ConnectionIndicator { get; } = new()
    {
        Width = 7, Height = 7, Fill = Brush(0xD9BB7A), Margin = new Thickness(0, 0, 6, 0)
    };
    public TextBlock QueueText { get; } = Label("", 12);
    public TextBlock ConnectionErrorText { get; } = Label("", 12);
    public TextBlock StatusDetail { get; } = Label("", 12);
    public Button RelayRetryButton { get; } = Button("配信を再試行");
    public Button OpenInputButton { get; } = Button("入力を開く");
    public Button UnreadButton { get; } = Button("新着");

    public DesktopPanels(Window owner, DataTemplate historyTemplate, Action openComposer,
        Action openHistory, Action openSettings, Action closeReply, Action previousReply,
        Action nextReply, Action openUnread)
    {
        _owner = owner;
        var hoverRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        hoverRow.Children.Add(Button("会話", openComposer));
        hoverRow.Children.Add(Button("履歴", openHistory));
        hoverRow.Children.Add(Button("設定", openSettings));
        Hover = Surface("操作", Frame(hoverRow, Brush(0x222B37), 6), 220, 48, () => SetVisible(Hover!, false));

        var replyHead = Header(_replyTitle, closeReply);
        _replyTitle.Foreground = Brush(0x77512F);
        _replyCount.Foreground = Brush(0x77512F);
        var nav = new WrapPanel { Margin = new Thickness(0, 2, 0, 5) };
        foreach (var button in new[] { _previous, _next, _newest })
        {
            button.Background = Brush(0xF1E2CA);
            button.Foreground = Brush(0x49362A);
            nav.Children.Add(button);
        }
        nav.Children.Insert(1, _replyCount);
        _replyCount.VerticalAlignment = VerticalAlignment.Center;
        _replyCount.Margin = new Thickness(6, 0, 6, 0);
        _previous.Click += (_, _) => previousReply();
        _next.Click += (_, _) => nextReply();
        _newest.Click += (_, _) => openUnread();
        var replyGrid = Rows(GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        Add(replyGrid, replyHead, 0);
        Add(replyGrid, nav, 1);
        Add(replyGrid, _replyBody, 2);
        var replyFrame = Frame(replyGrid, Brush(0xFFF7E5), 11, Brush(0xD4A55D));
        _bubbleRoot.Children.Add(_tail);
        _bubbleRoot.Children.Add(replyFrame);
        SetTail(OverlaySide.Right);
        Bubble = Surface("回答", _bubbleRoot, 380, 140, closeReply);

        SendButton.Background = Brush(0xA8D9C6);
        SendButton.Foreground = Brush(0x112C24);
        SendButton.IsEnabled = false;
        SendFeedbackText.Foreground = Brush(0xF4C397);
        SendFeedbackText.Visibility = Visibility.Collapsed;
        // Limit the feedback area so an error cannot consume the whole input surface.
        var feedback = new ScrollViewer
        {
            Content = SendFeedbackText, MaxHeight = 64,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var composerFoot = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        DockPanel.SetDock(SendButton, Dock.Right);
        composerFoot.Children.Add(SendButton);
        _composerHint.Foreground = Brush(0xB6C1D0);
        _composerHint.VerticalAlignment = VerticalAlignment.Center;
        composerFoot.Children.Add(_composerHint);
        var composerGrid = Rows(GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Add(composerGrid, Header(Label("羽衣ちゃんに話しかける"), () => SetVisible(Composer!, false)), 0);
        Add(composerGrid, feedback, 1);
        Add(composerGrid, new Border
        {
            Child = MessageInput, CornerRadius = new CornerRadius(7),
            BorderBrush = Brush(0x4E6278), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0)
        }, 2);
        Add(composerGrid, composerFoot, 3);
        Composer = Surface("会話入力", Frame(composerGrid, Brush(0x222B37), 11), 380, 166,
            () => SetVisible(Composer!, false));
        System.Windows.Automation.AutomationProperties.SetName(MessageInput, "羽衣ちゃんへのメッセージ");

        HistoryItems.ItemTemplate = historyTemplate;
        HistoryScroll.Content = HistoryItems;
        EmptyHistoryText.HorizontalAlignment = HorizontalAlignment.Center;
        EmptyHistoryText.VerticalAlignment = VerticalAlignment.Center;
        EmptyHistoryText.Foreground = Brush(0xB6C1D0);
        EmptyHistoryText.IsHitTestVisible = false;
        var historyBody = new Grid();
        historyBody.Children.Add(HistoryScroll);
        historyBody.Children.Add(EmptyHistoryText);
        NewMessagesButton.HorizontalAlignment = HorizontalAlignment.Center;
        NewMessagesButton.Visibility = Visibility.Collapsed;
        var historyGrid = Rows(GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Add(historyGrid, Header(Label("会話履歴"), () => SetVisible(History!, false)), 0);
        Add(historyGrid, historyBody, 1);
        Add(historyGrid, NewMessagesButton, 2);
        History = Surface("会話履歴", Frame(historyGrid, Brush(0x171C24), 12), 440, 430,
            () => SetVisible(History!, false));

        var connectionRow = new DockPanel();
        DockPanel.SetDock(ConnectionIndicator, Dock.Left);
        connectionRow.Children.Add(ConnectionIndicator);
        connectionRow.Children.Add(ConnectionText);
        ConnectionErrorText.Foreground = Brush(0xF4C397);
        StatusDetail.Foreground = Brush(0xF4C397);
        _statusContent.Children.Add(connectionRow);
        _statusContent.Children.Add(QueueText);
        _statusContent.Children.Add(ConnectionErrorText);
        _statusContent.Children.Add(StatusDetail);
        var statusActions = new WrapPanel();
        RelayRetryButton.Visibility = Visibility.Collapsed;
        UnreadButton.Visibility = Visibility.Collapsed;
        OpenInputButton.Visibility = Visibility.Collapsed;
        OpenInputButton.Click += (_, _) => openComposer();
        UnreadButton.Click += (_, _) => openUnread();
        statusActions.Children.Add(RelayRetryButton);
        statusActions.Children.Add(OpenInputButton);
        statusActions.Children.Add(UnreadButton);
        _statusContent.Children.Add(statusActions);
        Status = Surface("通知・状態", Frame(new ScrollViewer
        {
            Content = _statusContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        }, Brush(0x222B37), 8), 248, 76, () => SetVisible(Status!, false));
        _windows = [Hover, Bubble, Composer, History, Status];
    }

    public void Attach()
    {
        // The owner must already have been shown before assigning ownership.
        foreach (var window in _windows)
        {
            window.Owner = _owner;
            window.Topmost = _owner.Topmost;
        }
        _attached = true;
        Arrange();
    }

    public bool Wants(Window window) => _requested.Contains(window);

    public void SetVisible(Window window, bool visible)
    {
        if (_closing)
            return;
        var changed = visible ? _requested.Add(window) : _requested.Remove(window);
        if (changed)
        {
            Arrange();
            VisibilityChanged?.Invoke();
        }
    }

    public void FocusInput()
    {
        SetVisible(History, false);
        SetVisible(Composer, true);
        if (Composer.IsVisible)
        {
            Composer.Activate();
            MessageInput.Focus();
        }
    }

    public void FocusHistory()
    {
        SetVisible(History, true);
        if (History.IsVisible)
            History.Activate();
    }

    public void UpdateReply(string? title, string? body, int index, int total, int unreadCount)
    {
        _replyTitle.Text = title ?? "羽衣ちゃん";
        if (_replyBody.Text != (body ?? ""))
        {
            _replyBody.Text = body ?? "";
            _replyBody.ScrollToHome();
        }
        _replyCount.Text = total == 0 ? "0 / 0" : $"{index + 1} / {total}";
        _previous.IsEnabled = index > 0;
        _next.IsEnabled = index >= 0 && index + 1 < total;
        _newest.Content = unreadCount == 0 ? "最新" : $"新着 {unreadCount}";
        _newest.IsEnabled = total > 0;
        UnreadButton.Content = $"新着 {unreadCount}";
        UnreadButton.Visibility = unreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        Arrange();
    }

    public void SetTopmost(bool enabled)
    {
        foreach (var window in _windows)
            window.Topmost = enabled;
    }

    public void CloseAll()
    {
        _closing = true;
        _requested.Clear();
        foreach (var window in _windows)
            window.Close();
    }

    private static Brush Brush(uint rgb) => new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16),
        (byte)(rgb >> 8), (byte)rgb));

    private static TextBlock Label(string text, double fontSize = 14) => new()
    {
        Text = text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(0xF0F3F7)
    };

    private static TextBox ReadOnlyText(Brush foreground) => new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.Transparent, Foreground = foreground,
        BorderThickness = new Thickness(0), Padding = new Thickness(0),
        FontSize = 14
    };

    private static Button Button(string text, Action? action = null)
    {
        var button = new Button
        {
            Content = text, FontSize = 12, Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2), MinHeight = 26, Background = Brush(0x354457),
            Foreground = Brush(0xF0F3F7)
        };
        if (action is not null)
            button.Click += (_, _) => action();
        return button;
    }

    private static DockPanel Header(TextBlock title, Action close)
    {
        title.FontWeight = FontWeights.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var x = Button("×", close);
        x.ToolTip = "閉じる";
        DockPanel.SetDock(x, Dock.Right);
        row.Children.Add(x);
        row.Children.Add(title);
        return row;
    }

    private static Border Frame(UIElement child, Brush background, double padding, Brush? border = null) => new()
    {
        Background = background, BorderBrush = border ?? Brush(0x53657A), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Child = child
    };

    private static Grid Rows(params GridLength[] heights)
    {
        var grid = new Grid();
        foreach (var height in heights)
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        return grid;
    }

    private static void Add(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private Window Surface(string title, UIElement content, double width, double height, Action onClose)
    {
        var window = new Window
        {
            Title = "羽衣ちゃん - " + title, Width = width, Height = height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, FontFamily = new FontFamily("Yu Gothic UI, Meiryo"),
            FontSize = 14, Foreground = Brush(0xF0F3F7), UseLayoutRounding = true, Content = content
        };
        window.Closing += (_, e) =>
        {
            if (_closing)
                return;
            e.Cancel = true;
            window.Dispatcher.BeginInvoke(onClose);
        };
        return window;
    }

    private void SetTail(OverlaySide side)
    {
        _tail.HorizontalAlignment = HorizontalAlignment.Center;
        _tail.VerticalAlignment = VerticalAlignment.Center;
        _tail.Stretch = Stretch.Fill;
        _tail.Width = 12;
        _tail.Height = 20;
        switch (side)
        {
            case OverlaySide.Right:
                DockPanel.SetDock(_tail, Dock.Left);
                _tail.Points = new PointCollection { new(12, 0), new(0, 10), new(12, 20) };
                break;
            case OverlaySide.Left:
                DockPanel.SetDock(_tail, Dock.Right);
                _tail.Points = new PointCollection { new(0, 0), new(12, 10), new(0, 20) };
                break;
            case OverlaySide.Above:
                DockPanel.SetDock(_tail, Dock.Bottom);
                _tail.Width = 20;
                _tail.Height = 12;
                _tail.Points = new PointCollection { new(0, 0), new(10, 12), new(20, 0) };
                break;
            default:
                DockPanel.SetDock(_tail, Dock.Top);
                _tail.Width = 20;
                _tail.Height = 12;
                _tail.Points = new PointCollection { new(0, 12), new(10, 0), new(20, 12) };
                break;
        }
    }

    private double TextHeight(string text, double width, double fontSize)
    {
        var key = (text, width, fontSize);
        if (_measurements.TryGetValue(key, out var measured))
            return measured;
        var textBlock = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = _owner.FontFamily, FontSize = fontSize
        };
        textBlock.Measure(new Size(Math.Max(20, width), double.PositiveInfinity));
        if (_measurements.Count >= 12)
            _measurements.Clear();
        return _measurements[key] = textBlock.DesiredSize.Height;
    }

    // Clamp all overlays to the mascot's current work area. DPI and positions are
    // calculated in physical pixels; panel contents remain in WPF DIPs.
    public void Arrange()
    {
        if (!_attached || _closing || _arranging)
            return;
        _arranging = true;
        try
        {
            var scale = NativeDesktop.Scale(_owner);
            var area = NativeDesktop.WorkArea(_owner);
            var anchor = NativeDesktop.Bounds(_owner);
            int Px(double dip) => Math.Max(1, (int)Math.Ceiling(dip * scale));
            var gap = Px(8);
            var areaW = area.Width / scale;
            var areaH = area.Height / scale;
            var width = Math.Clamp(areaW - 16, 120, 390);
            var heightLimit = Math.Max(90, areaH - 16);
            var feedbackHeight = SendFeedbackText.Visibility == Visibility.Visible
                ? Math.Min(64, TextHeight(SendFeedbackText.Text, width - 26, 12)) : 0;
            Composer.Width = width;
            Composer.Height = Math.Min(heightLimit, 98 + Math.Clamp(
                TextHeight(MessageInput.Text, width - 34, 14) + 18, 54, 130) + feedbackHeight);
            Bubble.Width = width;
            var replyMaximum = Math.Max(92, Math.Min(heightLimit, areaH * .45));
            Bubble.Height = Math.Clamp(TextHeight(_replyBody.Text, width - 44, 14) + 87, 92, replyMaximum);
            if (Wants(Bubble) && Wants(Composer))
            {
                var available = Math.Max(2, heightLimit - 8);
                var replyFloor = Math.Min(92, available * .4);
                Composer.Height = Math.Min(Composer.Height, available - replyFloor);
                Bubble.Height = Math.Min(Bubble.Height, available - Composer.Height);
            }

            var primary = new List<Window>();
            if (Wants(Bubble)) primary.Add(Bubble);
            if (Wants(Composer)) primary.Add(Composer);
            var primaryWidth = Px(width);
            var primaryHeight = primary.Sum(window => Px(window.Height)) + Math.Max(0, primary.Count - 1) * gap;
            var group = OverlayLayout.Place(area, anchor, primaryWidth, Math.Max(1, primaryHeight), gap);
            SetTail(group.Side);
            if (group.Side == OverlaySide.Above && primary.Count == 2)
                primary.Reverse(); // the speech tail is closest to the character

            History.Width = Math.Clamp(areaW - 16, 120, 460);
            History.Height = Math.Clamp(areaH - 16, 100, 480);
            OverlaySide? opposite = group.Side switch
            {
                OverlaySide.Right => OverlaySide.Left,
                OverlaySide.Left => OverlaySide.Right,
                OverlaySide.Above => OverlaySide.Below,
                _ => OverlaySide.Above
            };
            var history = OverlayLayout.Place(area, anchor, Px(History.Width), Px(History.Height), gap, opposite);
            var suppressPrimary = Wants(History) && primary.Count > 0 && Intersects(history.Bounds, group.Bounds);
            var used = new List<ScreenRect> { anchor };
            if (!suppressPrimary && primary.Count > 0)
            {
                var y = group.Bounds.Y;
                foreach (var window in primary)
                {
                    var bounds = OverlayLayout.Clamp(area, new ScreenRect(group.Bounds.X, y, Px(window.Width), Px(window.Height)));
                    ShowAt(window, bounds);
                    used.Add(bounds);
                    y += Px(window.Height) + gap;
                }
            }
            else
            {
                HideIfVisible(Bubble);
                HideIfVisible(Composer);
            }
            if (!Wants(Bubble)) HideIfVisible(Bubble);
            if (!Wants(Composer)) HideIfVisible(Composer);
            if (Wants(History))
            {
                ShowAt(History, history.Bounds);
                used.Add(history.Bounds);
            }
            else HideIfVisible(History);

            Hover.Width = Math.Min(220, Math.Max(120, areaW - 16));
            Hover.Height = 48;
            if (Wants(Hover))
            {
                var bounds = FreePosition(area, anchor, Px(Hover.Width), Px(Hover.Height), gap, OverlaySide.Above, used);
                ShowAt(Hover, bounds);
                used.Add(bounds);
            }
            else HideIfVisible(Hover);

            Status.Width = Math.Clamp(areaW - 16, 120, 272);
            _statusContent.Measure(new Size(Math.Max(80, Status.Width - 22), double.PositiveInfinity));
            Status.Height = Math.Clamp(_statusContent.DesiredSize.Height + 20, 48, Math.Max(48, Math.Min(180, areaH - 16)));
            if (Wants(Status))
                ShowAt(Status, FreePosition(area, anchor, Px(Status.Width), Px(Status.Height), gap, OverlaySide.Below, used));
            else HideIfVisible(Status);
        }
        finally
        {
            _arranging = false;
        }
    }

    private static bool Intersects(ScreenRect a, ScreenRect b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    private static ScreenRect FreePosition(ScreenRect area, ScreenRect anchor, int width, int height,
        int gap, OverlaySide preferred, IReadOnlyList<ScreenRect> used)
    {
        var order = new[] { preferred, OverlaySide.Right, OverlaySide.Left, OverlaySide.Above, OverlaySide.Below };
        foreach (var side in order.Distinct())
        {
            var candidate = OverlayLayout.Place(area, anchor, width, height, gap, side).Bounds;
            if (used.All(item => !Intersects(candidate, item)))
                return candidate;
        }
        // Small or occupied work areas: keep the controls accessible within the area.
        foreach (var point in new[] { (area.X, area.Y), (area.Right - width, area.Y),
            (area.X, area.Bottom - height), (area.Right - width, area.Bottom - height) })
        {
            var candidate = OverlayLayout.Clamp(area, new ScreenRect(point.Item1, point.Item2, width, height));
            if (used.All(item => !Intersects(candidate, item)))
                return candidate;
        }
        return OverlayLayout.Place(area, anchor, width, height, gap, preferred).Bounds;
    }

    private void ShowAt(Window window, ScreenRect rect)
    {
        _ = NativeDesktop.Handle(window);
        if ((window == Hover || window == Status) && _hooked.Add(window))
            NativeDesktop.PreventMouseActivation(window);
        NativeDesktop.Move(window, rect.X, rect.Y);
        if (!window.IsVisible)
            window.Show();
        NativeDesktop.Move(window, rect.X, rect.Y);
    }

    private static void HideIfVisible(Window window)
    {
        if (window.IsVisible)
            window.Hide();
    }
}
