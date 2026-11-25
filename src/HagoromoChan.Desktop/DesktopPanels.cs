using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

// A single conversation surface contains the history stack and the editable draft.
// Its lower edge is anchored to a content-independent work-area zone. The controls
// at that edge stay in place even when the number or height of bubbles changes.
internal sealed class DesktopPanels
{
    private const double NavHeight = 44;
    private const double Gap = 6;
    private const int DraftLimit = 32768;
    private static readonly Brush Ink = ColorBrush(0x342D2A);
    private static readonly Brush Gold = ColorBrush(0xCFA76A);
    private static readonly Brush AssistantPaper = ColorBrush(0xFFF7E5);
    private static readonly Brush UserPaper = ColorBrush(0xF1F3E9);
    private static readonly Brush ActionPaper = ColorBrush(0xEBDDC4);
    private static readonly Brush Dark = ColorBrush(0x263343);
    private static readonly Brush White = ColorBrush(0xF0F3F7);
    private readonly Window _owner;
    private readonly Window[] _windows;
    private readonly HashSet<Window> _requested = [];
    private readonly HashSet<Window> _hooked = [];
    private readonly StackPanel _conversationRoot = new();
    private readonly StackPanel _historyStack = new();
    private readonly StackPanel _composerBody = new();
    private readonly TextBlock _draftPageLabel = Label("", 12);
    private readonly Button _draftPrevious = ActionButton("↑");
    private readonly Button _draftNext = ActionButton("↓");
    private readonly RepeatButton _older = Repeat("↑");
    private readonly RepeatButton _newer = Repeat("↓");
    private readonly Button _latest = ActionButton("最新");
    private readonly Button _inputToggle = ActionButton("入力");
    private readonly Button _closeConversation = ActionButton("×");
    private readonly TextBlock _historyUnavailable = Label("入力を閉じると履歴を表示できます。", 12);
    private readonly TextBox _probe = BodyBox();
    private readonly TextBlock _probeBlock = Label("");
    private readonly Dictionary<string, IReadOnlyList<TextSlice>> _historyPages = new(StringComparer.Ordinal);
    private readonly StackPanel _statusContent = new();
    private readonly Action<string> _markRead;
    private readonly Action _openUnread;
    private readonly Action _dismissAll;
    private readonly Action _openComposer;
    private bool _attached;
    private bool _closing;
    private bool _arranging;
    private bool _arrangeQueued;
    private bool _composerOpen;
    private bool _composing;
    private bool _assigningDraft;
    private string _draft = "";
    private string? _draftMeasuredText;
    private TextSlice _draftSlice;
    private IReadOnlyList<TextSlice> _draftPages = [];
    private int _draftCaret;
    private int? _forcedDraftOffset;
    private double _draftWidth;
    private double _draftMaximum;
    private int _wheelRemainder;
    private ChatMessage[] _history = [];
    private HashSet<string> _unread = new(StringComparer.Ordinal);
    private string? _anchorId;
    private int _anchorOffset;
    private double _historyWidth;
    private double _historyMaximum;
    private OverlaySide _side = OverlaySide.Right;
    private string _renderKey = "";
    private DockPanel? _composerBubble;

    public Window Hover { get; }
    public Window Conversation { get; }
    public Window Status { get; }
    public bool ConversationOpen => Wants(Conversation);
    public bool ComposerOpen => ConversationOpen && _composerOpen;
    public string DraftText => _draft;
    public event Action? VisibilityChanged;
    public event Action? DraftChanged;

    public TextBox MessageInput { get; } = BodyBox();
    public Button SendButton { get; } = ActionButton("送信");
    public TextBlock SendFeedbackText { get; } = Label("", 12);
    public TextBlock ConnectionText { get; } = Label("接続を確認しています", 12);
    public Ellipse ConnectionIndicator { get; } = new()
    {
        Width = 7, Height = 7, Fill = ColorBrush(0xD9BB7A), Margin = new Thickness(0, 0, 6, 0)
    };
    public TextBlock QueueText { get; } = Label("", 12);
    public TextBlock ConnectionErrorText { get; } = Label("", 12);
    public TextBlock StatusDetail { get; } = Label("", 12);
    public Button RelayRetryButton { get; } = ActionButton("配信を再試行");
    public Button OpenInputButton { get; } = ActionButton("入力");
    public Button UnreadButton { get; } = ActionButton("新着");

    public DesktopPanels(Window owner, Action openComposer, Action openHistory,
        Action openSettings, Action dismissAll, Action openUnread, Action<string> markRead)
    {
        _owner = owner;
        _openComposer = openComposer;
        _markRead = markRead;
        _openUnread = openUnread;
        _dismissAll = dismissAll;

        var hoverRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        hoverRow.Children.Add(ActionButton("会話", openComposer));
        hoverRow.Children.Add(ActionButton("履歴", openHistory));
        hoverRow.Children.Add(ActionButton("設定", openSettings));
        Hover = Surface("操作", Frame(hoverRow, AssistantPaper, 5), () => SetVisible(Hover!, false));

        MessageInput.IsReadOnly = false;
        MessageInput.MaxLength = DraftLimit;
        MessageInput.CaretBrush = Ink;
        System.Windows.Automation.AutomationProperties.SetName(MessageInput, "羽衣ちゃんへのメッセージ");
        MessageInput.TextChanged += EditorChanged;
        MessageInput.SelectionChanged += (_, _) =>
        {
            if (!_assigningDraft)
                _draftCaret = _draftSlice.Start + Math.Clamp(MessageInput.CaretIndex, 0, MessageInput.Text.Length);
        };
        SendButton.IsEnabled = false;
        SendButton.ToolTip = "Ctrl+Enterで送信、Enterで改行";
        SendButton.Background = ColorBrush(0xCFDFC8);
        SendFeedbackText.Foreground = ColorBrush(0x8F4F2C);
        SendFeedbackText.Visibility = Visibility.Collapsed;
        SendFeedbackText.MaxHeight = 38;
        SendFeedbackText.TextTrimming = TextTrimming.CharacterEllipsis;
        _composerBody.Children.Add(SendFeedbackText);
        _composerBody.Children.Add(MessageInput);
        var draftFooter = new Grid { Height = 34, Margin = new Thickness(0, 5, 0, 0) };
        AddColumns(draftFooter, 30, 30, -1, 84);
        _draftPrevious.ToolTip = "下書きの前ページ";
        _draftNext.ToolTip = "下書きの次ページ";
        _draftPrevious.Click += (_, _) => MoveDraft(-1);
        _draftNext.Click += (_, _) => MoveDraft(1);
        Put(draftFooter, _draftPrevious, 0);
        Put(draftFooter, _draftNext, 1);
        _draftPageLabel.VerticalAlignment = VerticalAlignment.Center;
        _draftPageLabel.Margin = new Thickness(5, 0, 0, 0);
        _draftPageLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _draftPageLabel.ToolTip = "Enterで改行、Ctrl+Enterで送信。長い下書きは入力ページを切り替えます。";
        Put(draftFooter, _draftPageLabel, 2);
        Put(draftFooter, SendButton, 3);
        _composerBody.Children.Add(draftFooter);

        _older.ToolTip = "上へ：ひとつ前の発言・ページを追加";
        _newer.ToolTip = "下へ：最も古い表示を外す／次ページ";
        _latest.ToolTip = "最初の未読、または最新の発言へ";
        _inputToggle.ToolTip = "入力欄の表示・非表示";
        _closeConversation.ToolTip = "すべての吹き出しを閉じる（未読・下書きは保持）";
        _older.Click += (_, _) => MoveHistory(-1);
        _newer.Click += (_, _) => MoveHistory(1);
        _latest.Click += (_, _) => _openUnread();
        _inputToggle.Click += (_, _) =>
        {
            if (_composerOpen) CloseComposer();
            else _openComposer();
        };
        _closeConversation.Click += (_, _) => _dismissAll();
        var navigation = new Grid { Height = NavHeight - 12 };
        AddStarColumns(navigation, 0.7, 0.7, 1.7, 1.3, 0.65);
        Put(navigation, _older, 0);
        Put(navigation, _newer, 1);
        Put(navigation, _latest, 2);
        Put(navigation, _inputToggle, 3);
        Put(navigation, _closeConversation, 4);
        var navFrame = Frame(navigation, AssistantPaper, 5);
        navFrame.Height = NavHeight;
        _conversationRoot.Children.Add(_historyStack);
        _historyUnavailable.Visibility = Visibility.Collapsed;
        _conversationRoot.Children.Add(_historyUnavailable);
        _conversationRoot.Children.Add(navFrame);
        _conversationRoot.VerticalAlignment = VerticalAlignment.Bottom;
        Conversation = Surface("会話", _conversationRoot, dismissAll);
        Conversation.PreviewMouseWheel += (_, e) =>
        {
            if (_history.Length == 0 || _composing)
                return;
            _wheelRemainder += e.Delta;
            while (Math.Abs(_wheelRemainder) >= 120)
            {
                var sign = Math.Sign(_wheelRemainder);
                _wheelRemainder -= sign * 120;
                MoveHistory(sign > 0 ? -1 : 1);
            }
            e.Handled = true;
        };

        var connectionRow = new DockPanel();
        DockPanel.SetDock(ConnectionIndicator, Dock.Left);
        connectionRow.Children.Add(ConnectionIndicator);
        connectionRow.Children.Add(ConnectionText);
        foreach (var label in new[] { ConnectionText, QueueText, ConnectionErrorText, StatusDetail })
        {
            label.Foreground = White;
            label.MaxHeight = 48;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        _statusContent.Children.Add(connectionRow);
        _statusContent.Children.Add(QueueText);
        _statusContent.Children.Add(ConnectionErrorText);
        _statusContent.Children.Add(StatusDetail);
        var actions = new WrapPanel();
        RelayRetryButton.Visibility = Visibility.Collapsed;
        UnreadButton.Visibility = Visibility.Collapsed;
        OpenInputButton.Visibility = Visibility.Collapsed;
        OpenInputButton.Click += (_, _) => openComposer();
        UnreadButton.Click += (_, _) => openUnread();
        actions.Children.Add(RelayRetryButton);
        actions.Children.Add(OpenInputButton);
        actions.Children.Add(UnreadButton);
        actions.Children.Add(ActionButton("詳細コピー", () => Copy(string.Join(Environment.NewLine,
            new[] { ConnectionText.Text, QueueText.Text, ConnectionErrorText.Text, StatusDetail.Text }))));
        _statusContent.Children.Add(actions);
        Status = Surface("通知・状態", Frame(_statusContent, Dark, 8), () => SetVisible(Status!, false));
        _windows = [Hover, Conversation, Status];
    }

    public void Attach()
    {
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
        if (_closing) return;
        var changed = visible ? _requested.Add(window) : _requested.Remove(window);
        if (changed)
        {
            Arrange();
            VisibilityChanged?.Invoke();
        }
    }

    public void HideSurfaces()
    {
        _requested.Clear();
        _composerOpen = false;
        _wheelRemainder = 0;
        foreach (var window in _windows)
            HideIfVisible(window);
        VisibilityChanged?.Invoke();
    }

    public void Open(string? messageId, bool composer, bool activate)
    {
        var wasOpen = ConversationOpen;
        if (messageId is not null)
            SetAnchor(messageId, 0);
        else if (!wasOpen || _anchorId is null)
            SelectLatest();
        _composerOpen = composer;
        _requested.Add(Conversation);
        Arrange();
        VisibilityChanged?.Invoke();
        if (activate && ComposerOpen && Conversation.IsVisible)
        {
            Conversation.Activate();
            MessageInput.Focus();
        }
    }

    public void FocusInput() => Open(null, true, true);

    public void FocusHistory() => Open(null, false, false);

    public void CloseComposer()
    {
        if (_composing) return;
        _composerOpen = false;
        Arrange();
        VisibilityChanged?.Invoke();
    }

    public void ShowMessage(string id)
    {
        Open(id, ComposerOpen, false);
    }

    public void UpdateHistory(IReadOnlyList<ChatMessage> messages, IEnumerable<string> unread)
    {
        _history = messages.ToArray();
        _unread = unread.ToHashSet(StringComparer.Ordinal);
        if (_anchorId is null || !_history.Any(m => m.Id == _anchorId))
            SelectLatest();
        _latest.Content = _unread.Count == 0 ? "最新" : $"新着 {_unread.Count}";
        UnreadButton.Content = $"新着 {_unread.Count}";
        UnreadButton.Visibility = _unread.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ConversationOpen)
            RequestArrange();
    }

    public void SelectLatest()
    {
        _anchorId = _history.LastOrDefault()?.Id;
        _anchorOffset = 0;
        RequestArrange();
    }

    private void SetAnchor(string id, int offset)
    {
        _anchorId = id;
        _anchorOffset = offset;
    }

    public void SetDraftText(string text)
    {
        _draft = text;
        _draftMeasuredText = null;
        _draftCaret = 0;
        _forcedDraftOffset = 0;
        // Retain the full draft independently of the current editable page.
        AssignEditor(new TextSlice(0, 0, 0), "", 0);
        RequestArrange();
        DraftChanged?.Invoke();
    }

    public void SetComposing(bool composing)
    {
        _composing = composing;
        if (!composing)
            RequestArrange();
    }

    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_assigningDraft) return;
        var replacement = MessageInput.Text;
        _draft = TextPaging.Replace(_draft, _draftSlice, replacement);
        _draftSlice = new TextSlice(_draftSlice.Start, replacement.Length, _draftSlice.Height);
        _draftCaret = _draftSlice.Start + Math.Clamp(MessageInput.CaretIndex, 0, replacement.Length);
        _draftMeasuredText = null;
        RequestArrange();
        DraftChanged?.Invoke();
    }

    private void AssignEditor(TextSlice slice, string text, int caret)
    {
        _assigningDraft = true;
        try
        {
            _draftSlice = slice;
            MessageInput.MaxLength = Math.Max(1, DraftLimit - (_draft.Length - slice.Length));
            if (MessageInput.Text != text)
                MessageInput.Text = text;
            if (MessageInput.CaretIndex != caret)
                MessageInput.CaretIndex = Math.Clamp(caret, 0, MessageInput.Text.Length);
        }
        finally { _assigningDraft = false; }
    }

    private double LayoutDraft(double width, double maximum)
    {
        if (_composing)
            return Math.Min(maximum, double.IsFinite(MessageInput.Height) ? Math.Max(1, MessageInput.Height) : 44);

        var reflow = _draftMeasuredText != _draft || _draftWidth != width || _draftMaximum != maximum;
        if (reflow)
        {
            _draftPages = TextPaging.Split(_draft, maximum, text => MeasureText(text, width));
            _draftMeasuredText = _draft;
            _draftWidth = width;
            _draftMaximum = maximum;
        }
        var forced = _forcedDraftOffset;
        var offset = forced ?? _draftCaret;
        _forcedDraftOffset = null;
        // Moving the caret to the end of an already displayed page must not
        // switch to the next page unless content reflows or navigation is explicit.
        var page = !reflow && forced is null
            ? TextPaging.PageAt(_draftPages, _draftSlice.Start)
            : TextPaging.PageAt(_draftPages, Math.Clamp(offset, 0, _draft.Length));
        var slice = _draftPages[page];
        var contents = slice.Read(_draft);
        var caret = Math.Clamp(_draftCaret - slice.Start, 0, contents.Length);
        if (_draftSlice.Start != slice.Start || _draftSlice.Length != slice.Length || MessageInput.Text != contents)
            AssignEditor(slice, contents, caret);
        else
        {
            _draftSlice = slice;
            MessageInput.MaxLength = Math.Max(1, DraftLimit - (_draft.Length - slice.Length));
        }
        _draftCaret = slice.Start + MessageInput.CaretIndex;
        var multiple = _draftPages.Count > 1;
        _draftPrevious.IsEnabled = page > 0;
        _draftNext.IsEnabled = page + 1 < _draftPages.Count;
        _draftPageLabel.Text = multiple ? $"入力 {page + 1}/{_draftPages.Count}" : "Ctrl+Enter";
        var height = Math.Min(maximum, Math.Max(44, slice.Height));
        MessageInput.Width = width;
        MessageInput.Height = height;
        return height;
    }

    private void MoveDraft(int direction)
    {
        if (_composing || _draftPages.Count == 0) return;
        // Reflow first if a text edit was queued before the navigation event.
        Arrange();
        var index = TextPaging.PageAt(_draftPages, _draftSlice.Start);
        var target = index + direction;
        if (target < 0 || target >= _draftPages.Count) return;
        var slice = _draftPages[target];
        _draftCaret = slice.Start;
        _forcedDraftOffset = slice.Start;
        Arrange();
        MessageInput.Focus();
    }

    private IReadOnlyList<TextSlice> Pages(int index)
    {
        var message = _history[index];
        if (_historyPages.TryGetValue(message.Id, out var pages)) return pages;
        pages = TextPaging.Split(message.Text, _historyMaximum, text => MeasureText(text, _historyWidth));
        _historyPages[message.Id] = pages;
        return pages;
    }

    private HistoryPosition? Position()
    {
        if (_history.Length == 0) return null;
        var index = Array.FindIndex(_history, m => m.Id == _anchorId);
        if (index < 0)
        {
            SelectLatest();
            index = _history.Length - 1;
        }
        return new HistoryPosition(index, TextPaging.PageAt(Pages(index), _anchorOffset));
    }

    private void MoveHistory(int direction)
    {
        if (_composing || _history.Length == 0 || !ConversationOpen)
            return;
        if (_historyMaximum <= 0 && _composerOpen)
        {
            _composerOpen = false;
            Arrange();
            VisibilityChanged?.Invoke();
        }
        if (_historyMaximum <= 0) return;
        var current = Position();
        if (current is not { } position) return;
        var next = direction < 0
            ? HistoryNavigation.Older(position, i => Pages(i).Count)
            : HistoryNavigation.Newer(position, _history.Length, i => Pages(i).Count);
        if (next is not { } target) return;
        SetAnchor(_history[target.Message].Id, Pages(target.Message)[target.Page].Start);
        Arrange();
    }

    private void RenderHistory(double width, double available, OverlaySide side)
    {
        var vertical = side is OverlaySide.Above or OverlaySide.Below;
        var chrome = vertical ? 74.0 : 62.0; // header, border/padding, tail, card gap
        var bodyWidth = Math.Max(36, width - 42);
        var minimum = Math.Max(44, MeasureText("あ😀", bodyWidth));
        var maximum = Math.Floor(available - chrome);
        if (maximum < minimum || _history.Length == 0)
        {
            _historyStack.Children.Clear();
            _renderKey = "";
            _historyMaximum = 0;
            _historyUnavailable.Visibility = _history.Length != 0 && _composerOpen
                ? Visibility.Visible : Visibility.Collapsed;
            _older.IsEnabled = _newer.IsEnabled = _history.Length != 0 && _composerOpen;
            return;
        }

        _historyUnavailable.Visibility = Visibility.Collapsed;
        if (_historyWidth != bodyWidth || _historyMaximum != maximum)
        {
            _historyPages.Clear();
            _historyWidth = bodyWidth;
            _historyMaximum = maximum;
        }
        var pos = Position();
        if (pos is not { } first) return;
        _older.IsEnabled = HistoryNavigation.Older(first, i => Pages(i).Count) is not null;
        _newer.IsEnabled = HistoryNavigation.Newer(first, _history.Length, i => Pages(i).Count) is not null;
        var visible = HistoryNavigation.Fit(first, _history.Length, i => Pages(i).Count,
            p => Pages(p.Message)[p.Page].Height + chrome, available);
        var key = string.Join("|", visible.Select(p =>
        {
            var message = _history[p.Message];
            var page = Pages(p.Message)[p.Page];
            return $"{message.Id}:{page.Start}:{page.Length}:{page.Height}:{p.Page}:{Pages(p.Message).Count}:{_unread.Contains(message.Id)}";
        })) + $"/{width}/{side}/{chrome}";
        if (key == _renderKey) return;
        _renderKey = key;
        _historyStack.Children.Clear();
        foreach (var p in visible)
        {
            var message = _history[p.Message];
            var pages = Pages(p.Message);
            var slice = pages[p.Page];
            var user = message.Role == "user";
            var role = user ? "あなた" : message.Role == "assistant" ? "羽衣ちゃん" : "お知らせ";
            var title = $"{role}　{message.CreatedAt.LocalDateTime:M/d HH:mm}";
            if (pages.Count > 1) title += $"　{p.Page + 1}/{pages.Count}";
            var header = new DockPanel { Height = 27, Margin = new Thickness(0, 0, 0, 3) };
            var copy = ActionButton("コピー", () => Copy(message.Text));
            copy.FontSize = 11;
            DockPanel.SetDock(copy, Dock.Right);
            header.Children.Add(copy);
            if (_unread.Contains(message.Id))
            {
                var read = ActionButton("既読", () => _markRead(message.Id));
                read.FontSize = 11;
                DockPanel.SetDock(read, Dock.Right);
                header.Children.Add(read);
            }
            var label = Label(title, 12);
            label.FontWeight = FontWeights.SemiBold;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.TextWrapping = TextWrapping.NoWrap;
            header.Children.Add(label);
            var body = BodyBox();
            body.IsReadOnly = true;
            body.Text = slice.Read(message.Text);
            body.Width = bodyWidth;
            body.Height = slice.Height;
            var content = new StackPanel();
            content.Children.Add(header);
            content.Children.Add(body);
            var bubble = Bubble(content, user ? UserPaper : AssistantPaper, user ? Opposite(side) : side);
            bubble.Height = slice.Height + chrome - Gap;
            bubble.Margin = new Thickness(0, 0, 0, Gap);
            _historyStack.Children.Add(bubble);
        }
    }

    private double MeasureText(string text, double width)
    {
        _probe.Width = width;
        _probe.Text = text;
        _probe.Measure(new Size(width, double.PositiveInfinity));
        _probeBlock.Text = text;
        _probeBlock.Width = width;
        _probeBlock.Measure(new Size(width, double.PositiveInfinity));
        // Both the native TextBox and a wrapping TextBlock are measured; the small
        // extra allowance covers line/caret and pixel rounding differences.
        return Math.Ceiling(Math.Max(_probe.DesiredSize.Height, _probeBlock.DesiredSize.Height)) + 8;
    }

    public void RequestArrange()
    {
        if (!_attached || _closing || _arrangeQueued) return;
        _arrangeQueued = true;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _arrangeQueued = false;
            Arrange();
        }));
    }

    public void Arrange()
    {
        if (!_attached || _closing || _arranging) return;
        _arranging = true;
        try
        {
            var scale = NativeDesktop.Scale(_owner);
            var area = NativeDesktop.WorkArea(_owner);
            var anchor = NativeDesktop.Bounds(_owner);
            int Px(double dip) => Math.Max(1, (int)Math.Ceiling(dip * scale));
            var gap = Px(8);
            var used = new List<ScreenRect> { anchor };
            if (ConversationOpen)
            {
                var zone = OverlayLayout.ConversationZone(area, anchor, scale);
                _side = zone.Side;
                var width = zone.Bounds.Width / scale;
                var maxHeight = zone.Bounds.Height / scale;
                var bodyWidth = Math.Max(36, width - 42);
                var vertical = _side is OverlaySide.Above or OverlaySide.Below;
                var composerChrome = vertical ? 84.0 : 72.0;
                var composerHeight = 0.0;
                var available = Math.Max(0, maxHeight - NavHeight - 2);
                if (_composerOpen)
                {
                    // Keep room for history on normal screens; on narrow work areas
                    // the input/history toggle provides the full area to one view.
                    var minimumLine = Math.Max(44, MeasureText("あ😀", bodyWidth));
                    if (available < composerChrome + minimumLine)
                    {
                        _composerOpen = false;
                        if (_composerBubble is not null)
                            _composerBubble.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        try
                        {
                            var feedback = SendFeedbackText.Visibility == Visibility.Visible
                                ? Math.Min(38.0, Math.Max(0, available - composerChrome - minimumLine)) : 0;
                            SendFeedbackText.MaxHeight = feedback;
                            var cap = _history.Length == 0 || available < 320
                                ? available
                                : Math.Max(130, available * .34);
                            cap = Math.Min(available, Math.Max(cap, composerChrome + feedback + minimumLine));
                            var maxText = Math.Max(minimumLine, Math.Floor(cap - composerChrome - feedback));
                            var editorHeight = LayoutDraft(bodyWidth, maxText);
                            composerHeight = editorHeight + composerChrome + feedback;
                            SendFeedbackText.ToolTip = SendFeedbackText.Text;
                            if (_composerBubble is null)
                            {
                                _composerBubble = Bubble(_composerBody, UserPaper, Opposite(_side));
                                _conversationRoot.Children.Insert(_conversationRoot.Children.Count - 1, _composerBubble);
                            }
                            else
                                SetTail(_composerBubble, Opposite(_side));
                            _composerBubble.Visibility = Visibility.Visible;
                            _composerBubble.Height = Math.Max(1, composerHeight - Gap);
                            _composerBubble.Margin = new Thickness(0, 0, 0, Gap);
                        }
                        catch (InvalidOperationException)
                        {
                            // Never discard or clip a draft that cannot fit this monitor.
                            // The full buffer is kept; the user can reopen it in a larger area.
                            _composerOpen = false;
                            composerHeight = 0;
                            if (_composerBubble is not null)
                                _composerBubble.Visibility = Visibility.Collapsed;
                            SendFeedbackText.Text = "入力を表示できる領域を確保してから開き直してください。下書きは保持しています。";
                            SendFeedbackText.Visibility = Visibility.Visible;
                            RequestArrange();
                        }
                    }
                }
                else if (_composerBubble is not null)
                    _composerBubble.Visibility = Visibility.Collapsed;

                var historyRoom = Math.Max(0, available - composerHeight);
                // Reserve the small fallback hint only when it can fit.
                try
                {
                    _historyUnavailable.Text = "入力を閉じると履歴を表示できます。";
                    RenderHistory(width, historyRoom, _side);
                }
                catch (InvalidOperationException)
                {
                    // No text may be silently clipped to a page that is physically
                    // too small. Retain the original messages and allow a larger
                    // work area / history-only mode to reflow them.
                    _historyStack.Children.Clear();
                    _renderKey = "";
                    _historyPages.Clear();
                    _historyMaximum = 0;
                    _historyUnavailable.Text = "本文が収まる表示領域を確保してください。";
                    _historyUnavailable.Visibility = Visibility.Visible;
                    _older.IsEnabled = _newer.IsEnabled = false;
                }
                if (_historyUnavailable.Visibility == Visibility.Visible && historyRoom < 24)
                    _historyUnavailable.Visibility = Visibility.Collapsed;
                _inputToggle.Content = _composerOpen ? "履歴のみ" : "入力";
                _latest.IsEnabled = _history.Length > 0;
                _conversationRoot.Width = width;
                _conversationRoot.Measure(new Size(width, double.PositiveInfinity));
                var actualHeight = Math.Min(zone.Bounds.Height, Px(_conversationRoot.DesiredSize.Height));
                Conversation.Width = width;
                Conversation.Height = actualHeight / scale;
                var bounds = new ScreenRect(zone.Bounds.X, zone.Bounds.Bottom - actualHeight,
                    zone.Bounds.Width, actualHeight);
                ShowAt(Conversation, bounds);
                // Anchor the native bottom edge as well, including DPI rounding.
                var actual = NativeDesktop.Bounds(Conversation);
                NativeDesktop.Move(Conversation, bounds.X, zone.Bounds.Bottom - actual.Height);
                used.Add(new ScreenRect(bounds.X, zone.Bounds.Bottom - actual.Height, actual.Width, actual.Height));
            }
            else HideIfVisible(Conversation);

            if (Wants(Hover))
            {
                var width = Math.Min(220, area.Width / scale);
                Hover.Width = width;
                Hover.Height = 46;
                var bounds = FreePosition(area, anchor, Px(width), Px(46), gap, OverlaySide.Above, used);
                ShowAt(Hover, bounds);
                used.Add(bounds);
            }
            else HideIfVisible(Hover);

            if (Wants(Status))
            {
                var width = Math.Min(300, area.Width / scale);
                Status.Width = width;
                _statusContent.Measure(new Size(Math.Max(1, width - 18), double.PositiveInfinity));
                var height = Math.Min(area.Height / scale, Math.Max(46, _statusContent.DesiredSize.Height + 18));
                Status.Height = height;
                ShowAt(Status, FreePosition(area, anchor, Px(width), Px(height), gap, OverlaySide.Below, used));
            }
            else HideIfVisible(Status);
        }
        finally { _arranging = false; }
    }

    public void SetTopmost(bool enabled)
    {
        foreach (var window in _windows) window.Topmost = enabled;
    }

    public void CloseAll()
    {
        _closing = true;
        _requested.Clear();
        foreach (var window in _windows) window.Close();
    }

    private Window Surface(string title, UIElement content, Action onClose)
    {
        var window = new Window
        {
            Title = "羽衣ちゃん - " + title, Width = 320, Height = 120,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            FontFamily = new FontFamily("Yu Gothic UI, Meiryo"), FontSize = 14,
            UseLayoutRounding = true, Content = content
        };
        window.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            window.Dispatcher.BeginInvoke(onClose);
        };
        return window;
    }

    private void ShowAt(Window window, ScreenRect rect)
    {
        _ = NativeDesktop.Handle(window);
        if ((window == Hover || window == Status) && _hooked.Add(window))
            NativeDesktop.PreventMouseActivation(window);
        NativeDesktop.Move(window, rect.X, rect.Y);
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        NativeDesktop.Move(window, rect.X, rect.Y);
    }

    private static void HideIfVisible(Window window)
    {
        if (window.IsVisible) window.Hide();
    }

    private static bool Intersects(ScreenRect a, ScreenRect b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    private static ScreenRect FreePosition(ScreenRect area, ScreenRect anchor, int width, int height,
        int gap, OverlaySide preferred, IReadOnlyList<ScreenRect> used)
    {
        foreach (var side in new[] { preferred, OverlaySide.Right, OverlaySide.Left, OverlaySide.Above, OverlaySide.Below }.Distinct())
        {
            var candidate = OverlayLayout.Place(area, anchor, width, height, gap, side).Bounds;
            if (used.All(item => !Intersects(candidate, item))) return candidate;
        }
        foreach (var point in new[] { (area.X, area.Y), (area.Right - width, area.Y),
            (area.X, area.Bottom - height), (area.Right - width, area.Bottom - height) })
        {
            var candidate = OverlayLayout.Clamp(area, new ScreenRect(point.Item1, point.Item2, width, height));
            if (used.All(item => !Intersects(candidate, item))) return candidate;
        }
        return OverlayLayout.Place(area, anchor, width, height, gap, preferred).Bounds;
    }

    private static OverlaySide Opposite(OverlaySide side) => side switch
    {
        OverlaySide.Left => OverlaySide.Right,
        OverlaySide.Right => OverlaySide.Left,
        OverlaySide.Above => OverlaySide.Below,
        _ => OverlaySide.Above
    };

    private static DockPanel Bubble(UIElement content, Brush paper, OverlaySide side)
    {
        var root = new DockPanel();
        var tail = new Polygon { Fill = paper, Stroke = Gold, StrokeThickness = 1 };
        root.Children.Add(tail);
        root.Children.Add(Frame(content, paper, 9));
        SetTail(root, side);
        return root;
    }

    private static void SetTail(DockPanel root, OverlaySide side)
    {
        var tail = (Polygon)root.Children[0];
        tail.HorizontalAlignment = HorizontalAlignment.Center;
        tail.VerticalAlignment = VerticalAlignment.Center;
        tail.Stretch = Stretch.Fill;
        tail.Width = 12;
        tail.Height = 20;
        switch (side)
        {
            case OverlaySide.Right:
                DockPanel.SetDock(tail, Dock.Left);
                tail.Points = new PointCollection { new(12, 0), new(0, 10), new(12, 20) };
                break;
            case OverlaySide.Left:
                DockPanel.SetDock(tail, Dock.Right);
                tail.Points = new PointCollection { new(0, 0), new(12, 10), new(0, 20) };
                break;
            case OverlaySide.Above:
                DockPanel.SetDock(tail, Dock.Bottom);
                tail.Width = 20;
                tail.Height = 12;
                tail.Points = new PointCollection { new(0, 0), new(10, 12), new(20, 0) };
                break;
            default:
                DockPanel.SetDock(tail, Dock.Top);
                tail.Width = 20;
                tail.Height = 12;
                tail.Points = new PointCollection { new(0, 12), new(10, 0), new(20, 12) };
                break;
        }
    }

    private static Brush ColorBrush(uint rgb) => new SolidColorBrush(Color.FromRgb(
        (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    private static TextBlock Label(string text, double fontSize = 14) => new()
    {
        Text = text, FontSize = fontSize, FontFamily = new FontFamily("Yu Gothic UI, Meiryo"),
        TextWrapping = TextWrapping.Wrap, Foreground = Ink
    };

    private static TextBox BodyBox() => new()
    {
        TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, FontSize = 14,
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.Transparent, Foreground = Ink,
        BorderThickness = new Thickness(0), Padding = new Thickness(0)
    };

    private static void StyleButton(ButtonBase button, string text)
    {
        button.Content = text;
        button.FontSize = 12;
        button.Padding = new Thickness(5, 3, 5, 3);
        button.Margin = new Thickness(2);
        button.Background = ActionPaper;
        button.Foreground = Ink;
        button.BorderThickness = new Thickness(0);
        button.Cursor = Cursors.Hand;
        button.Focusable = false;
    }

    private static Button ActionButton(string text, Action? action = null)
    {
        var button = new Button();
        StyleButton(button, text);
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private static RepeatButton Repeat(string text)
    {
        var button = new RepeatButton { Delay = 350, Interval = 120 };
        StyleButton(button, text);
        return button;
    }

    private static Border Frame(UIElement child, Brush background, double padding) => new()
    {
        Background = background, BorderBrush = Gold, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(11), Padding = new Thickness(padding), Child = child
    };

    private static void AddColumns(Grid grid, params double[] widths)
    {
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0
                ? new GridLength(-width, GridUnitType.Star) : new GridLength(width) });
    }

    private static void AddStarColumns(Grid grid, params double[] widths)
    {
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
    }

    private static void Put(Grid grid, UIElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show("クリップボードを使用できません。再試行してください。", "羽衣ちゃん");
        }
    }
}
