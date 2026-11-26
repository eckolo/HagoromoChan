using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

// Independently requested panels share a transparent conversation surface.
// Stable native bounds and retained history cards avoid recreating the visible stack.
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
    private readonly StackPanel _conversationRoot = new() { ClipToBounds = true };
    private readonly StackPanel _historyStack = new();
    private readonly StackPanel _composerBody = new();
    private readonly TextBlock _draftPageLabel = Label("", 12);
    private readonly Button _draftPrevious = ActionButton("↑");
    private readonly Button _draftNext = ActionButton("↓");
    private readonly RepeatButton _older = Repeat("↑");
    private readonly RepeatButton _newer = Repeat("↓");
    private readonly Border _navFrame;
    private readonly Button _closeConversation = ActionButton("×");
    private readonly TextBlock _historyUnavailable = Label("入力を閉じると履歴を表示できます。", 12);
    private readonly Dictionary<string, IReadOnlyList<TextSlice>> _historyPages = new(StringComparer.Ordinal);
    private readonly StackPanel _statusContent = new();
    private readonly Action<IReadOnlyList<string>> _markRead;
    private bool _activatingComposer;
    private bool _attached;
    private bool _closing;
    private bool _arranging;
    private bool _arrangeQueued;
    private ConversationVisibility _visibility;
    private bool _composing;
    private bool _assigningDraft;
    private string _draft = "";
    private int _wheelRemainder;
    private ChatMessage[] _history = [];
    private HashSet<string> _unread = new(StringComparer.Ordinal);
    private string? _anchorId;
    private int _anchorOffset;
    private double _historyWidth;
    private double _historyMaximum;
    private double _historyScale;
    private OverlaySide _side = OverlaySide.Right;
    private readonly List<HistoryCardKey> _renderedKeys = [];
    private readonly Dictionary<HistoryCardKey, HistoryCard> _cards = [];
    private readonly Dictionary<string, HistoryCard> _liveHistory = new(StringComparer.Ordinal);
    private sealed record HistoryCard(DockPanel Card, Button Read, TextBlock Header, string Title,
        TextBox Body, LiveHistoryViewport Viewport);
    private readonly record struct HistoryCardKey(string Id, int Start, int Length, double Height,
        int Page, int PageCount, double Width, double Scale, OverlaySide Side);
    private DockPanel? _composerBubble;

    public Window Conversation { get; }
    public Window Status { get; }
    public bool ConversationOpen => _visibility.Any;
    public bool ComposerOpen => _visibility.Input;
    public bool HistoryOpen => _visibility.History;
    public bool StatusNeedsAlternateAccess => Wants(Status) && !Status.IsVisible;
    public string DraftText => _draft;
    public event Action? VisibilityChanged;
    public event Action? DraftChanged;

    public TextBox MessageInput { get; } = BodyBox();
    public Button SendButton { get; } = ActionButton("➤");
    public TextBlock SendFeedbackText { get; } = Label("", 12);
    public TextBlock ConnectionText { get; } = Label("接続を確認しています", 12);
    public Ellipse ConnectionIndicator { get; } = new()
    {
        Width = 7, Height = 7, Fill = ColorBrush(0xD9BB7A), Margin = new Thickness(0, 0, 6, 0)
    };
    public TextBlock QueueText { get; } = Label("", 12);
    public TextBlock ConnectionErrorText { get; } = Label("", 12);
    public TextBlock StatusDetail { get; } = Label("", 12);
    public TextBlock RuntimeSummary { get; } = Label("", 12);
    public TextBlock RuntimeDetail { get; } = Label("", 12);
    public Button RuntimeToggleButton { get; } = SymbolButton("⌄", "接続状態の詳細を開く");
    public Button RelayRetryButton { get; } = ActionButton("配信を再試行");
    public Button OpenInputButton { get; } = ActionButton("入力");
    public Button UnreadButton { get; } = ActionButton("新着");

    public DesktopPanels(Window owner, Action dismissAll, Action openUnread, Action<IReadOnlyList<string>> markRead, Action openComposer)
    {
        _owner = owner;
        _markRead = markRead;

        MessageInput.IsReadOnly = false;
        MessageInput.MaxLength = DraftLimit;
        MessageInput.CaretBrush = Ink;
        System.Windows.Automation.AutomationProperties.SetName(MessageInput, "羽衣ちゃんへのメッセージ");
        MessageInput.TextChanged += EditorChanged;
        SendButton.IsEnabled = false;
        DescribeButton(SendButton, "送信（Ctrl+Enter）、Enterで改行");
        SendButton.Background = ColorBrush(0xCFDFC8);
        SendFeedbackText.Foreground = ColorBrush(0x8F4F2C);
        SendFeedbackText.Visibility = Visibility.Collapsed;
        SendFeedbackText.MaxHeight = 38;
        SendFeedbackText.TextTrimming = TextTrimming.CharacterEllipsis;
        _composerBody.Children.Add(SendFeedbackText);
        _composerBody.Children.Add(MessageInput);
        var draftFooter = new Grid { Height = 34, Margin = new Thickness(0, 5, 0, 0) };
        AddColumns(draftFooter, 30, 30, -1, 84);
        DescribeButton(_draftPrevious, "下書きの前ページ");
        DescribeButton(_draftNext, "下書きの次ページ");
        _draftPrevious.Click += (_, _) => MoveDraft(-1);
        _draftNext.Click += (_, _) => MoveDraft(1);
        Put(draftFooter, _draftPrevious, 0);
        Put(draftFooter, _draftNext, 1);
        _draftPageLabel.VerticalAlignment = VerticalAlignment.Center;
        _draftPageLabel.Margin = new Thickness(5, 0, 0, 0);
        _draftPageLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _draftPageLabel.ToolTip = "Enterで改行、Ctrl+Enterで送信。長い下書きは入力欄の中で移動できます。";
        Put(draftFooter, _draftPageLabel, 2);
        Put(draftFooter, SendButton, 3);
        _composerBody.Children.Add(draftFooter);

        DescribeButton(_older, "上へ：前の本文ページまたは発言");
        DescribeButton(_newer, "下へ：次の本文ページまたは発言");
        DescribeButton(_closeConversation, "すべての吹き出しを閉じる（未読・下書きは保持）");
        _older.Click += (_, _) => MoveHistory(-1);
        _newer.Click += (_, _) => MoveHistory(1);
        _closeConversation.Click += (_, _) => dismissAll();
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var button in new ButtonBase[] { _older, _newer, _closeConversation })
        {
            button.Width = 32;
            button.Height = 30;
            button.Padding = new Thickness(0);
            button.FontSize = 17;
            button.FontFamily = new FontFamily("Segoe UI Symbol, Yu Gothic UI");
            button.Background = ColorBrush(0x415267);
            button.Foreground = White;
            navigation.Children.Add(button);
        }
        _navFrame = Frame(navigation, Dark, 4);
        _navFrame.BorderBrush = ColorBrush(0x53657A);
        _navFrame.Width = 118;
        _navFrame.Height = NavHeight;
        _conversationRoot.Children.Add(_historyStack);
        _historyUnavailable.Visibility = Visibility.Collapsed;
        _conversationRoot.Children.Add(_historyUnavailable);
        _conversationRoot.Children.Add(_navFrame);
        _conversationRoot.VerticalAlignment = VerticalAlignment.Bottom;
        Conversation = Surface("会話", _conversationRoot, dismissAll);
        Conversation.Activated += (_, _) =>
        {
            if (!_activatingComposer && HistoryOpen)
                QueueHistoryRead(HistoryReadGesture.Activation, [], ignoreComposerFocus: false);
        };
        Conversation.PreviewMouseWheel += (_, e) =>
        {
            if (_composing)
                return;
            if (_composerBubble?.IsMouseOver == true)
            {
                if (MessageInput.IsMouseOver && ScrollBody(MessageInput, e.Delta))
                    e.Handled = true;
                return;
            }
            if (!HistoryOpen || _history.Length == 0)
                return;
            // Consume wheel input inside a clipped native text viewport only
            // when it can scroll further. At its edge, the same wheel pages.
            foreach (var entry in _cards.Values)
                if (entry.Body.IsMouseOver && ScrollBody(entry.Body, e.Delta))
                {
                    e.Handled = true;
                    return;
                }
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
        RuntimeSummary.Foreground = White;
        RuntimeSummary.Visibility = Visibility.Collapsed;
        _statusContent.Children.Add(RuntimeSummary);
        RuntimeDetail.Foreground = White;
        RuntimeDetail.Visibility = Visibility.Collapsed;
        _statusContent.Children.Add(RuntimeDetail);
        var actions = new WrapPanel();
        RelayRetryButton.Visibility = Visibility.Collapsed;
        UnreadButton.Visibility = Visibility.Collapsed;
        OpenInputButton.Visibility = Visibility.Collapsed;
        OpenInputButton.Click += (_, _) => openComposer();
        UnreadButton.Click += (_, _) => openUnread();
        actions.Children.Add(RelayRetryButton);
        actions.Children.Add(OpenInputButton);
        actions.Children.Add(UnreadButton);
        actions.Children.Add(RuntimeToggleButton);
        actions.Children.Add(SymbolButton("⧉", "状態の詳細をコピー", CopyStatus));
        _statusContent.Children.Add(actions);
        Status = Surface("通知・状態", Frame(_statusContent, Dark, 8), () => SetVisible(Status!, false));
        _windows = [Conversation, Status];
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

    public (double X, double Y) UnreadDirection(ScreenRect mascot)
    {
        // Only retained, currently displayed cards are candidates. The transparent history
        // window spans most of the work area, so its centre is not the bubble's position.
        FrameworkElement? target = null;
        if (Conversation.IsVisible && HistoryOpen)
        {
            foreach (var key in _renderedKeys)
                if (_unread.Contains(key.Id) && _cards.TryGetValue(key, out var entry)
                    && entry.Card.IsVisible && entry.Card.IsArrangeValid)
                {
                    target = entry.Card;
                    break;
                }
        }
        if (target is null && Status.IsVisible && UnreadButton.IsVisible && UnreadButton.IsArrangeValid)
            target = UnreadButton;
        if (target is null || target.ActualWidth <= 0 || target.ActualHeight <= 0
            || PresentationSource.FromVisual(target) is null)
            return default;

        var point = target.PointToScreen(new Point(target.ActualWidth * .5, target.ActualHeight * .5));
        var dx = point.X - (mascot.X + mascot.Width * .5);
        var dy = point.Y - (mascot.Y + mascot.Height * .25);
        var length = double.Hypot(dx, dy);
        return length >= 1 ? (dx / length, dy / length) : default;
    }

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
        _visibility = default;
        _wheelRemainder = 0;
        foreach (var window in _windows)
            HideIfVisible(window);
        VisibilityChanged?.Invoke();
    }

    private void SetConversation(ConversationVisibility visibility, bool activate, string? messageId = null)
    {
        if (_closing) return;
        // Preserve an active IME composition when toggling only the input.
        if (_composing && ComposerOpen && !visibility.Input) return;
        var wasOpen = ConversationOpen;
        if (messageId is not null)
            SetAnchor(messageId, 0);
        else if (visibility.History && (!wasOpen || _anchorId is null))
            SelectLatest();

        _visibility = visibility;
        if (visibility.Any) _requested.Add(Conversation);
        else _requested.Remove(Conversation);
        Arrange();
        VisibilityChanged?.Invoke();
        if (activate && ComposerOpen && Conversation.IsVisible)
        {
            _activatingComposer = true;
            try
            {
                Conversation.Activate();
                MessageInput.Focus();
            }
            finally { _activatingComposer = false; }
        }
    }

    public void OpenBoth() => SetConversation(new ConversationVisibility(true, true), true);

    public void ToggleInput() => SetConversation(_visibility.ToggleInput(), !ComposerOpen);

    public void ToggleHistory() => SetConversation(_visibility.ToggleHistory(), false);

    public void FocusInput() => SetConversation(_visibility with { Input = true }, true);

    public void CloseComposer() => SetConversation(_visibility with { Input = false }, false);

    // Receiving a reply opens history, never forces the input open or closed.
    public void ShowMessage(string id) => SetConversation(_visibility with { History = true }, false, id);

    public void OpenUnreadMessage(string id)
    {
        ShowMessage(id);
        if (!HistoryOpen || !Conversation.IsVisible || !_renderedKeys.Any(key => key.Id == id))
            return;
        // Unlike a receive, the status button explicitly asks to read history.
        // It also works if the shared window was already active for input.
        if (Conversation.IsActive || Conversation.Activate())
            QueueHistoryRead(HistoryReadGesture.Activation, [], ignoreComposerFocus: true);
    }

    public void CopyStatus() => Copy(string.Join(Environment.NewLine,
        new[] { ConnectionText.Text, QueueText.Text, ConnectionErrorText.Text, StatusDetail.Text, RuntimeDetail.Text }));

    public void SetRuntimeDetails(bool expanded, string text, string summary)
    {
        RuntimeSummary.Text = summary;
        RuntimeSummary.Visibility = !expanded && summary.Length != 0 ? Visibility.Visible : Visibility.Collapsed;
        RuntimeDetail.Text = text;
        RuntimeDetail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RuntimeToggleButton.Content = expanded ? "⌃" : "⌄";
        DescribeButton(RuntimeToggleButton, expanded ? "接続状態の詳細を閉じる" : "接続状態の詳細を開く");
    }

    public void UpdateHistory(IReadOnlyList<ChatMessage> messages, IEnumerable<string> unread)
    {
        var unreadSet = unread.ToHashSet(StringComparer.Ordinal);
        if (_history.SequenceEqual(messages) && _unread.SetEquals(unreadSet))
            return;
        _history = messages.ToArray();
        _unread = unreadSet;
        if (_anchorId is null || !_history.Any(m => m.Id == _anchorId))
            SelectLatest();
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
        _assigningDraft = true;
        try
        {
            MessageInput.Text = text;
            MessageInput.CaretIndex = 0;
            MessageInput.ScrollToHome();
        }
        finally { _assigningDraft = false; }
        RequestArrange();
        DraftChanged?.Invoke();
    }

    public void SetComposing(bool composing)
    {
        _composing = composing;
        if (!composing) RequestArrange();
    }

    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_assigningDraft) return;
        _draft = MessageInput.Text;
        RequestArrange();
        DraftChanged?.Invoke();
    }

    private double LayoutDraft(double width, double maximum, double scale)
    {
        MessageInput.Width = MessageInput.MaxWidth = width;
        if (!_composing)
        {
            // The actual editor keeps its text, selection and native IME.
            // Measure this same template at its live wrap width, not a clone.
            MessageInput.Height = double.NaN;
            MessageInput.Measure(new Size(width, double.PositiveInfinity));
        }

        var natural = _composing && double.IsFinite(MessageInput.Height)
            ? MessageInput.Height : BubbleLayout.RoundUp(MessageInput.DesiredSize.Height, scale);
        var height = Math.Min(maximum, Math.Max(44, natural));
        MessageInput.Height = height;
        var scrollable = natural > height || MessageInput.ExtentHeight > MessageInput.ViewportHeight;
        _draftPrevious.IsEnabled = _draftNext.IsEnabled = scrollable;
        _draftPageLabel.Text = scrollable ? "入力欄をスクロール" : "Ctrl+Enter";
        return height;
    }

    private void MoveDraft(int direction)
    {
        if (_composing) return;
        MessageInput.Focus();
        var edge = direction < 0 ? MessageInput.GetFirstVisibleLineIndex() : MessageInput.GetLastVisibleLineIndex();
        if (edge < 0) return;
        var line = Math.Clamp(edge + direction, 0, Math.Max(0, MessageInput.LineCount - 1));
        var index = MessageInput.GetCharacterIndexFromLineIndex(line);
        if (index >= 0)
        {
            MessageInput.CaretIndex = index;
            MessageInput.ScrollToLine(line);
        }
    }

    private IReadOnlyList<TextSlice> Pages(int index)
    {
        var message = _history[index];
        if (_historyPages.TryGetValue(message.Id, out var pages)) return pages;
        if (!_liveHistory.TryGetValue(message.Id, out var card))
        {
            card = BuildHistoryCard(message, _historyWidth, _side);
            _liveHistory.Add(message.Id, card);
        }
        // The same visual instance is attached before its one full layout.
        // WPF uses the conversation's DPI and actual content template.
        if (!_historyStack.Children.Contains(card.Card))
            _historyStack.Children.Add(card.Card);
        pages = card.Viewport.Layout(_historyWidth, _historyMaximum, _historyScale)
            .Select(page => page.Slice).ToArray();
        _historyPages[message.Id] = pages;
        if (card.Viewport.BeginOneGeometryRetry())
            QueueLiveGeometryRetry(message.Id, card.Viewport);
        return pages;
    }

    private void QueueLiveGeometryRetry(string id, LiveHistoryViewport viewport)
    {
        Conversation.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closing || !Conversation.IsVisible || !_liveHistory.TryGetValue(id, out var current)
                || !ReferenceEquals(current.Viewport, viewport))
                return;
            viewport.InvalidateUnverifiedLayout();
            _historyPages.Remove(id);
            RequestArrange();
        }));
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
        if (_composing || _history.Length == 0 || !HistoryOpen || _historyMaximum <= 0)
            return;
        var current = Position();
        if (current is not { } position) return;
        if (_liveHistory.TryGetValue(_history[position.Message].Id, out var entry) &&
            !entry.Viewport.GeometryKnown && ScrollBody(entry.Body, -direction * 120))
            return;
        var next = direction < 0
            ? HistoryNavigation.Older(position, i => Pages(i).Count)
            : HistoryNavigation.Newer(position, _history.Length, i => Pages(i).Count);
        if (next is not { } target) return;
        var before = RenderedHistoryPages();
        SetAnchor(_history[target.Message].Id, Pages(target.Message)[target.Page].Start);
        Arrange();
        QueueHistoryRead(HistoryReadGesture.Navigation, before, ignoreComposerFocus: true);
        VisibilityChanged?.Invoke();
    }

    private HistoryPageIdentity[] RenderedHistoryPages() =>
        _renderedKeys.Select(key => new HistoryPageIdentity(key.Id, key.Start, key.Length)).ToArray();

    private void QueueHistoryRead(HistoryReadGesture gesture, HistoryPageIdentity[] before, bool ignoreComposerFocus)
    {
        if (_closing || !Conversation.IsVisible || !HistoryOpen)
            return;
        // Remember only pages offered by this gesture. A pending receive/layout
        // cannot expand the set before WPF commits the arrange.
        var offered = RenderedHistoryPages();
        if (offered.Length == 0 || (gesture == HistoryReadGesture.Navigation && before.SequenceEqual(offered)))
            return;
        Conversation.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_closing || !Conversation.IsVisible || !HistoryOpen)
                return;
            var composerFocused = !ignoreComposerFocus && _composerBody.IsKeyboardFocusWithin;
            var active = Conversation.IsActive;
            var laidOut = _renderedKeys.Where(key =>
                _cards.TryGetValue(key, out var entry) && IsLaidOutHistoryCard(entry.Card))
                .Select(key => new HistoryPageIdentity(key.Id, key.Start, key.Length)).ToHashSet();
            var targets = HistoryReadPolicy.Targets(gesture, active, composerFocused, before, offered,
                laidOut, _unread);
            if (targets.Length > 0)
                _markRead(targets);
        }));
    }

    private bool IsLaidOutHistoryCard(FrameworkElement card)
    {
        if (!card.IsVisible || !card.IsArrangeValid || card.ActualWidth <= 0 || card.ActualHeight <= 0
            || PresentationSource.FromVisual(card) is null)
            return false;
        var origin = card.TranslatePoint(new Point(0, 0), Conversation);
        // An overflowed/clipped card is not evidence of viewing.
        return origin.X >= -1 && origin.X + card.ActualWidth <= Conversation.ActualWidth + 1
            && origin.Y >= -1 && origin.Y + card.ActualHeight <= Conversation.ActualHeight + 1;
    }

    private void ClearHistory()
    {
        _historyStack.Children.Clear();
        _renderedKeys.Clear();
        _cards.Clear();
        _liveHistory.Clear();
        _historyPages.Clear();
    }

    private void RenderHistory(double bodyWidth, double available, OverlaySide side, double scale)
    {
        var chrome = BubbleLayout.HistoryChrome(side, Gap, scale);
        var maximum = BubbleLayout.HistoryBodyBudget(available, chrome, scale);
        if (!HistoryOpen || _history.Length == 0 || bodyWidth < BubbleLayout.MinimumBodyWidth
            || maximum < 44)
        {
            ClearHistory();
            _historyMaximum = 0;
            _historyUnavailable.Visibility = HistoryOpen && _history.Length != 0 && ComposerOpen
                ? Visibility.Visible : Visibility.Collapsed;
            _older.IsEnabled = _newer.IsEnabled = false;
            return;
        }

        _historyUnavailable.Visibility = Visibility.Collapsed;
        if (_historyWidth != bodyWidth || _historyMaximum != maximum || _historyScale != scale || _side != side)
        {
            ClearHistory();
            _historyPages.Clear();
            _historyWidth = bodyWidth;
            _historyMaximum = maximum;
            _historyScale = scale;
        }
        _side = side;
        var pos = Position();
        if (pos is not { } first) return;
        var needsFallback = !(_liveHistory.TryGetValue(_history[first.Message].Id, out var firstCard) &&
            firstCard.Viewport.GeometryKnown);
        _older.IsEnabled = needsFallback || HistoryNavigation.Older(first, i => Pages(i).Count) is not null;
        _newer.IsEnabled = needsFallback || HistoryNavigation.Newer(first, _history.Length, i => Pages(i).Count) is not null;
        var visible = new List<HistoryPosition>();
        var offered = new HashSet<int>();
        var used = 0.0;
        for (HistoryPosition? posToAdd = first; posToAdd is { } item;
            posToAdd = HistoryNavigation.Newer(item, _history.Length, i => Pages(i).Count))
        {
            // One physical TextBox can display only one viewport at a time.
            if (!offered.Add(item.Message)) break;
            var needed = Pages(item.Message)[item.Page].Height + chrome;
            if (used + needed > available) break;
            visible.Add(item);
            used += needed;
        }
        var desired = visible.Select(position =>
        {
            var message = _history[position.Message];
            var pages = Pages(position.Message);
            var page = pages[position.Page];
            return (Position: position, Key: new HistoryCardKey(message.Id, page.Start, page.Length,
                page.Height, position.Page, pages.Count, bodyWidth, scale, side));
        }).ToArray();
        var wanted = desired.Select(item => (UIElement)_liveHistory[item.Key.Id].Card).ToHashSet();
        for (var i = _historyStack.Children.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_historyStack.Children[i]))
                _historyStack.Children.RemoveAt(i);
        _renderedKeys.Clear();
        _cards.Clear();
        for (var i = 0; i < desired.Length; i++)
        {
            var item = desired[i];
            var key = item.Key;
            var card = _liveHistory[key.Id];
            var at = _historyStack.Children.IndexOf(card.Card);
            if (at != i)
            {
                if (at >= 0) _historyStack.Children.RemoveAt(at);
                _historyStack.Children.Insert(i, card.Card);
            }
            card.Viewport.Select(card.Viewport.Layout(bodyWidth, maximum, scale)[key.Page]);
            card.Header.Text = card.Title + (key.PageCount > 1 ? $"　{key.Page + 1}/{key.PageCount}" : "") +
                (card.Viewport.GeometryKnown ? "" : "　本文内スクロール");
            card.Read.Visibility = _unread.Contains(key.Id) ? Visibility.Visible : Visibility.Collapsed;
            _renderedKeys.Add(key);
            _cards.Add(key, card);
        }
        var retained = _renderedKeys.Select(key => key.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _liveHistory.Keys.Where(id => !retained.Contains(id)).ToArray())
        {
            _liveHistory.Remove(id);
            _historyPages.Remove(id);
        }
    }

    private HistoryCard BuildHistoryCard(ChatMessage message, double bodyWidth, OverlaySide side)
    {
        var user = message.Role == "user";
        var role = user ? "あなた" : message.Role == "assistant"
            ? message.InReplyTo is null ? "羽衣ちゃん・自発" : "羽衣ちゃん"
            : "お知らせ";
        var title = $"{role}　{message.CreatedAt.LocalDateTime:M/d HH:mm}";
        var header = new DockPanel { Height = BubbleLayout.HistoryHeader, Margin = new Thickness(0, 0, 0, BubbleLayout.HistoryHeaderGap) };
        var copy = SymbolButton("⧉", "この発言の全文をコピー", () => Copy(message.Text));
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        var read = SymbolButton("✓", "この回答を既読にする", () => _markRead([message.Id]));
        read.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(read, Dock.Right);
        header.Children.Add(read);
        var label = Label(title, 12);
        label.FontWeight = FontWeights.SemiBold;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.TextWrapping = TextWrapping.NoWrap;
        header.Children.Add(label);
        var body = BodyBox();
        body.IsReadOnly = true;
        body.Text = message.Text;
        body.Width = body.MaxWidth = bodyWidth;
        var viewport = new LiveHistoryViewport(body);
        viewport.GeometryInvalidated += () => QueueLiveGeometryRetry(message.Id, viewport);
        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(viewport);
        var bubble = Bubble(content, user ? UserPaper : AssistantPaper, user ? Opposite(side) : side);
        bubble.Margin = new Thickness(0, 0, 0, Gap);
        return new(bubble, read, label, title, body, viewport);
    }

    private static bool ScrollBody(TextBox box, int wheelDelta)
    {
        var maximum = box.ExtentHeight - box.ViewportHeight;
        if (wheelDelta == 0 || !double.IsFinite(maximum) || maximum <= .01)
            return false;
        var offset = box.VerticalOffset;
        var next = Math.Clamp(offset - wheelDelta * (48.0 / 120), 0, maximum);
        if (Math.Abs(next - offset) <= .01)
            return false;
        box.ScrollToVerticalOffset(next);
        return true;
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
            var ownerScale = NativeDesktop.Scale(_owner);
            var area = NativeDesktop.WorkArea(_owner);
            var anchor = NativeDesktop.Bounds(_owner);
            int Px(double dip) => Math.Max(1, (int)Math.Ceiling(dip * ownerScale));
            var gap = Px(8);
            var zone = OverlayLayout.ConversationZone(area, anchor, ownerScale);
            if (ConversationOpen)
            {
                // Establish the destination HWND/DPI first. The owner may still
                // be on another display, and its DIP is not the text viewport's.
                ShowAt(Conversation, zone.Bounds);
                var scale = NativeDesktop.Scale(Conversation);
                var (clientWidth, clientHeight) = NativeDesktop.ClientSize(Conversation);
                var width = BubbleLayout.ClientDip(zone.Bounds.Width, clientWidth, scale);
                var maxHeight = BubbleLayout.ClientDip(zone.Bounds.Height, clientHeight, scale);
                var bodyWidth = BubbleLayout.BodyWidth(width, scale);
                var vertical = zone.Side is OverlaySide.Above or OverlaySide.Below;
                var composerChrome = vertical ? 84.0 : 72.0;
                var composerHeight = 0.0;
                var available = BubbleLayout.ConversationBodyBudget(maxHeight, NavHeight, scale);
                if (ComposerOpen)
                {
                    var minimumLine = bodyWidth >= BubbleLayout.MinimumBodyWidth ? 44 : double.PositiveInfinity;
                    if (available < composerChrome + minimumLine)
                    {
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
                            var historyReserve = HistoryOpen && _history.Length != 0
                                ? BubbleLayout.HistoryChrome(_side, Gap, scale) + minimumLine + 1 / scale
                                : 0;
                            var maxText = BubbleLayout.ComposerBodyBudget(available, composerChrome,
                                feedback, minimumLine, historyReserve, scale);
                            if (maxText < minimumLine)
                                throw new InvalidOperationException("The available display area cannot fit the editor.");
                            var editorHeight = LayoutDraft(bodyWidth, maxText, scale);
                            composerHeight = editorHeight + composerChrome + feedback;
                            SendFeedbackText.ToolTip = SendFeedbackText.Text;
                            if (_composerBubble is null)
                            {
                                _composerBubble = Bubble(_composerBody, UserPaper, Opposite(zone.Side));
                                _conversationRoot.Children.Insert(_conversationRoot.Children.Count - 1, _composerBubble);
                            }
                            else
                                SetTail(_composerBubble, Opposite(zone.Side));
                            _composerBubble.Visibility = Visibility.Visible;
                            _composerBubble.Height = Math.Max(1, composerHeight - Gap);
                            _composerBubble.Margin = new Thickness(0, 0, 0, Gap);
                        }
                        catch (InvalidOperationException)
                        {
                            // Never discard or clip a draft that cannot fit this monitor.
                            // The full buffer is kept; the user can reopen it in a larger area.
                            composerHeight = 0;
                            if (_composerBubble is not null)
                                _composerBubble.Visibility = Visibility.Collapsed;
                            SendFeedbackText.Text = "入力を表示できる領域を確保してから開き直してください。下書きは保持しています。";
                            SendFeedbackText.Visibility = Visibility.Visible;
                        }
                    }
                }
                else if (_composerBubble is not null)
                    _composerBubble.Visibility = Visibility.Collapsed;

                var historyRoom = Math.Max(0, available - composerHeight);
                // Reserve the small fallback hint only when it can fit.
                try
                {
                    _historyUnavailable.Text = "右クリックで入力を閉じると履歴を表示できます。";
                    RenderHistory(bodyWidth, historyRoom, zone.Side, scale);
                }
                catch (InvalidOperationException)
                {
                    // No text may be silently clipped to a page that is physically
                    // too small. Retain the original messages and allow a larger
                    // work area / history-only mode to reflow them.
                    ClearHistory();
                    _historyPages.Clear();
                    _historyMaximum = 0;
                    _historyUnavailable.Text = "本文が収まる表示領域を確保してください。";
                    _historyUnavailable.Visibility = Visibility.Visible;
                    _older.IsEnabled = _newer.IsEnabled = false;
                }
                if (_historyUnavailable.Visibility == Visibility.Visible && historyRoom < 24)
                    _historyUnavailable.Visibility = Visibility.Collapsed;
                _navFrame.HorizontalAlignment = _side == OverlaySide.Left ? HorizontalAlignment.Right
                    : _side == OverlaySide.Right ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                _conversationRoot.Width = width;
                _conversationRoot.MaxHeight = maxHeight;
                // Keep the transparent window's established anchor, but only
                // lay out content in its measured in-bounds client rectangle.
                ShowAt(Conversation, zone.Bounds);
                if (NativeDesktop.Scale(Conversation) != scale ||
                    NativeDesktop.ClientSize(Conversation) != (clientWidth, clientHeight))
                    RequestArrange();
            }
            else HideIfVisible(Conversation);

            // There is no hover menu or invisible reservation for it. Put
            // status directly next to the mascot, below if the work area permits.
            if (Wants(Status))
            {
                var statusWidth = OverlayLayout.StatusWidth(area, anchor);
                _statusContent.Measure(new Size(Math.Max(1, statusWidth / ownerScale - 18), double.PositiveInfinity));
                var statusHeight = Math.Min(area.Height, Px(Math.Max(46, _statusContent.DesiredSize.Height + 18)));
                // Do not cover an open conversation to fit status. The context menu
                // still exposes full diagnostics when a nearby slot is unavailable.
                var status = OverlayLayout.StatusPlacement(area, anchor, zone, statusWidth, statusHeight, gap,
                    Conversation.IsVisible);
                if (status is { } bounds) ShowAt(Status, bounds);
                else HideIfVisible(Status);
            }
            else HideIfVisible(Status);
        }
        finally { _arranging = false; }
    }

    public void SetTopmost(bool enabled)
    {
        foreach (var window in _windows) window.Topmost = enabled;
    }

    // The owner and Application.Shutdown close the owned windows. Do not call
    // Window.Close again on each child from inside the owner's Closing event.
    public void PrepareForShutdown()
    {
        _closing = true;
        _requested.Clear();
        _visibility = default;
        _wheelRemainder = 0;
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

    private double PositionAt(Window window, ScreenRect rect)
    {
        _ = NativeDesktop.Handle(window);
        if (window == Status && _hooked.Add(window))
            NativeDesktop.PreventMouseActivation(window);
        // Moving the HWND can change its effective DPI. Convert the final
        // rectangle to WPF dimensions using the destination, not the owner.
        NativeDesktop.SetBounds(window, rect);
        var dpi = NativeDesktop.Scale(window);
        if (Math.Abs(window.Width - rect.Width / dpi) > .001) window.Width = rect.Width / dpi;
        if (Math.Abs(window.Height - rect.Height / dpi) > .001) window.Height = rect.Height / dpi;
        NativeDesktop.SetBounds(window, rect);
        return dpi;
    }

    private void ShowAt(Window window, ScreenRect rect)
    {
        // An unchanged, already-visible surface requires no repeated WPF layout.
        if (window.IsVisible && NativeDesktop.Bounds(window) == rect)
            return;
        PositionAt(window, rect);
        if (!window.IsVisible)
        {
            window.Show();
            // WPF may amend the initial DPI and HWND bounds on Show.
            PositionAt(window, rect);
        }
    }

    private static void HideIfVisible(Window window)
    {
        if (window.IsVisible) window.Hide();
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
        root.Children.Add(Frame(content, paper, BubbleLayout.FramePadding));
        SetTail(root, side);
        return root;
    }

    private static void SetTail(DockPanel root, OverlaySide side)
    {
        if (root.Tag is OverlaySide previous && previous == side) return;
        root.Tag = side;
        var tail = (Polygon)root.Children[0];
        // Keep the established DockPanel measurement and leave an equal gutter
        // opposite the ornament. Only the tail switches sides, not the frame.
        var frame = (Border)root.Children[1];
        var depth = BubbleLayout.TailDepth;
        frame.Margin = side switch
        {
            OverlaySide.Right => new Thickness(0, 0, depth, 0),
            OverlaySide.Left => new Thickness(depth, 0, 0, 0),
            _ => new Thickness(depth, 0, depth, 0)
        };
        tail.HorizontalAlignment = HorizontalAlignment.Center;
        tail.VerticalAlignment = VerticalAlignment.Center;
        tail.Stretch = Stretch.Fill;
        tail.Width = depth;
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
                tail.Height = depth;
                tail.Points = new PointCollection { new(0, 0), new(10, 12), new(20, 0) };
                break;
            default:
                DockPanel.SetDock(tail, Dock.Top);
                tail.Width = 20;
                tail.Height = depth;
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
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        UseLayoutRounding = true, ClipToBounds = true,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.Transparent, Foreground = Ink,
        BorderThickness = new Thickness(0), Padding = new Thickness(0)
    };

    private static void StyleButton(ButtonBase button, string text)
    {
        button.Template = (ControlTemplate)Application.Current.FindResource("OverlayButtonTemplate");
        ToolTipService.SetShowOnDisabled(button, true);
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

    private static Button SymbolButton(string symbol, string description, Action? action = null)
    {
        var button = ActionButton(symbol, action);
        button.FontFamily = new FontFamily("Segoe UI Symbol, Yu Gothic UI");
        button.FontSize = 15;
        button.Width = 32;
        button.Padding = new Thickness(0);
        DescribeButton(button, description);
        return button;
    }

    private static void DescribeButton(ButtonBase button, string description)
    {
        button.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(button, description);
    }

    private static RepeatButton Repeat(string text)
    {
        var button = new RepeatButton { Delay = 350, Interval = 120 };
        StyleButton(button, text);
        return button;
    }

    private static Border Frame(UIElement child, Brush background, double padding) => new()
    {
        Background = background, BorderBrush = Gold, BorderThickness = new Thickness(BubbleLayout.FrameBorder),
        CornerRadius = new CornerRadius(11), Padding = new Thickness(padding), Child = child
    };

    private static void AddColumns(Grid grid, params double[] widths)
    {
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0
                ? new GridLength(-width, GridUnitType.Star) : new GridLength(width) });
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
