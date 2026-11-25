using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MessageDisplay> _messages = [];
    private readonly HashSet<long> _seenSequences = [];
    private readonly RelayClient _relay;
    private readonly PendingMessageStore _pendingStore;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _relayGate = new(1, 1);
    private SendMessageRequest? _pending;
    private long _cursor;
    private bool _historyLoaded;
    private bool _sending;
    private bool _pendingAccepted;
    private bool _storageBlocked;
    private bool _imeComposing;
    private bool _compositionJustEnded;
    private bool _relayRetrying;

    private readonly DesktopPreferencesStore _preferencesStore;
    private readonly DesktopPreferences _preferences;
    private readonly DesktopPanels _panels;
    private readonly double _assetRatio;
    private readonly string? _assetWarning;
    private string? _preferencesWarning;
    private bool _preferencesSaveBlocked;
    private readonly DispatcherTimer _hoverIn = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _hoverOut = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _initialized;
    private bool _closing;
    private bool _pointerDown;
    private bool _dragging;
    private (int X, int Y) _dragStart;
    private ScreenRect _dragAnchor;
    private ScreenRect? _lastBounds;
    private ScreenRect? _lastArea;
    private double _lastScale;
    private ContextMenu? _contextMenu;
    private string? _replyId;
    private RelayStatus? _status;
    private bool _connectionProblem;
    private bool _historyOpened;

    // Keep the existing message acceptance, pagination and retry logic independent of
    // the actual surface containing its controls.
    private TextBox MessageInput => _panels.MessageInput;
    private Button SendButton => _panels.SendButton;
    private TextBlock SendFeedbackText => _panels.SendFeedbackText;
    private ItemsControl HistoryItems => _panels.HistoryItems;
    private ScrollViewer HistoryScroll => _panels.HistoryScroll;
    private TextBlock EmptyHistoryText => _panels.EmptyHistoryText;
    private Button NewMessagesButton => _panels.NewMessagesButton;
    private TextBlock ConnectionText => _panels.ConnectionText;
    private Ellipse ConnectionIndicator => _panels.ConnectionIndicator;
    private TextBlock QueueText => _panels.QueueText;
    private TextBlock ConnectionErrorText => _panels.ConnectionErrorText;
    private Button RelayRetryButton => _panels.RelayRetryButton;

    public MainWindow(string dataDirectory)
    {
        InitializeComponent();
        _relay = new RelayClient(dataDirectory);
        _pendingStore = new PendingMessageStore(dataDirectory);
        _preferencesStore = new DesktopPreferencesStore(dataDirectory);
        try
        {
            _preferences = _preferencesStore.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Never silently overwrite a damaged preferences file (including unread state).
            _preferences = new DesktopPreferences();
            _preferencesSaveBlocked = true;
            _preferencesWarning = "表示設定を読み込めません。desktop-ui.json を確認してください。 " + error.Message;
        }

        var artwork = MascotArtwork.Load(dataDirectory, out var assetWarning);
        _assetWarning = assetWarning;
        MascotImage.Source = artwork;
        _assetRatio = (double)artwork.PixelWidth / artwork.PixelHeight;
        Height = _preferences.Height;
        Width = Height * _assetRatio;
        Topmost = _preferences.Topmost;

        _panels = new DesktopPanels(this, (DataTemplate)FindResource("HistoryMessageTemplate"),
            OpenComposer, OpenHistory, OpenSettings, CloseReply, () => MoveReply(-1),
            () => MoveReply(1), OpenUnread);
        HistoryItems.ItemsSource = _messages;
        MessageInput.PreviewKeyDown += MessageInput_PreviewKeyDown;
        MessageInput.TextChanged += MessageInput_TextChanged;
        MessageInput.LostKeyboardFocus += MessageInput_LostKeyboardFocus;
        SendButton.Click += SendButton_Click;
        RelayRetryButton.Click += RelayRetryButton_Click;
        NewMessagesButton.Click += NewMessagesButton_Click;
        _panels.VisibilityChanged += RefreshStatus;
        WireDesktopEvents();

        TextCompositionManager.AddPreviewTextInputStartHandler(MessageInput, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(MessageInput, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(MessageInput, (_, _) => EndComposition());
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeDesktop.PreventMouseActivation(this);
            _panels.Attach();
            RestorePosition();
            _initialized = true;
            _placementTimer.Start();
            await RestorePendingAsync();
            RefreshStatus();
            await PollAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
            return;
        _closing = true;
        _hoverIn.Stop();
        _hoverOut.Stop();
        _placementTimer.Stop();
        if (_initialized)
            RememberPosition();
        _panels.CloseAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _relay.Dispose();
        base.OnClosed(e);
    }
}
