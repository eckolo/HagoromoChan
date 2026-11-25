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
    private readonly int _assetPixelWidth;
    private readonly int _assetPixelHeight;
    private readonly string? _assetWarning;
    private string? _preferencesWarning;
    private bool _preferencesSaveBlocked;
    private readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _initialized;
    private bool _closing;
    private bool _exitRequested;
    private bool _localExitQueued;
    private bool _controlRequest;
    private bool _expandedStatus;
    private readonly string _runtimeDirectory;
    private readonly string? _managerPipe = Environment.GetEnvironmentVariable(RuntimeControl.PipeVariable);
    private readonly string? _managedSession = Environment.GetEnvironmentVariable(RuntimeControl.SessionVariable);
    private RuntimeSnapshot? _runtime;
    private bool _pointerDown;
    private bool _dragging;
    private (int X, int Y) _dragStart;
    private ScreenRect _dragAnchor;
    private ScreenRect? _lastBounds;
    private ScreenRect? _lastArea;
    private double _lastScale;
    private ContextMenu? _contextMenu;
    private bool _compactSuppressed;
    private RelayStatus? _status;
    private bool _connectionProblem;

    // Keep the existing message acceptance, pagination and retry logic independent of
    // the actual surface containing its controls.
    private TextBox MessageInput => _panels.MessageInput;
    private Button SendButton => _panels.SendButton;
    private TextBlock SendFeedbackText => _panels.SendFeedbackText;
    private TextBlock ConnectionText => _panels.ConnectionText;
    private Ellipse ConnectionIndicator => _panels.ConnectionIndicator;
    private TextBlock QueueText => _panels.QueueText;
    private TextBlock ConnectionErrorText => _panels.ConnectionErrorText;
    private Button RelayRetryButton => _panels.RelayRetryButton;

    public MainWindow(string dataDirectory)
    {
        InitializeComponent();
        _runtimeDirectory = dataDirectory;
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

        var artwork = MascotArtwork.Load(dataDirectory, out var assetWarning, out var bundledArtwork);
        _reactionArtwork = new CharacterReactionArtwork(artwork, dataDirectory, bundledArtwork);
        _assetWarning = _reactionArtwork.Warning ?? assetWarning;
        MascotImage.Source = artwork;
        _assetPixelWidth = artwork.PixelWidth;
        _assetPixelHeight = artwork.PixelHeight;
        Height = Math.Min(_preferences.Height, _assetPixelHeight);
        Width = Height * _assetPixelWidth / _assetPixelHeight;
        Topmost = _preferences.Topmost;

        _panels = new DesktopPanels(this, DismissSurfaces, OpenUnread, MarkRead, OpenComposer);
        _panels.RuntimeToggleButton.Click += (_, _) => ToggleConnectionDetails();
        MessageInput.PreviewKeyDown += MessageInput_PreviewKeyDown;
        _panels.DraftChanged += RefreshComposer;
        MessageInput.LostKeyboardFocus += MessageInput_LostKeyboardFocus;
        SendButton.Click += SendButton_Click;
        RelayRetryButton.Click += RelayRetryButton_Click;
        _panels.VisibilityChanged += RefreshStatus;
        WireDesktopEvents();

        TextCompositionManager.AddPreviewTextInputStartHandler(MessageInput, (_, _) => BeginComposition());
        TextCompositionManager.AddPreviewTextInputUpdateHandler(MessageInput, (_, _) => BeginComposition());
        TextCompositionManager.AddPreviewTextInputHandler(MessageInput, (_, _) => EndComposition());
        Loaded += MainWindow_Loaded;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(CheckPlacement));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeDesktop.PreventMouseActivation(this);
            _panels.Attach();
            RestorePosition();
            _initialized = true;
            StartReactions();
            _placementTimer.Start();
            await RestorePendingAsync();
            RefreshStatus();
            await PollAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void RequestExit()
    {
        if (string.IsNullOrEmpty(_managerPipe))
            RequestLocalExit(); // Standalone development/debug mode.
        else
            RequestManagedControl(RuntimeCommand.Stop);
    }

    private async void RequestManagedControl(RuntimeCommand command)
    {
        if (_exitRequested || _closing || _controlRequest || string.IsNullOrEmpty(_managerPipe))
            return;
        _controlRequest = true;
        RefreshComposer();
        try
        {
            if (!await RuntimeControl.SendAsync(_managerPipe, command, _lifetime.Token))
                ShowSendFeedback("管理プロセスが処理中です。終了・再起動を再試行してください。");
            else
            {
                _exitRequested = true;
                StopReactions();
                _placementTimer.Stop();
                RefreshComposer();
                // The supervisor owns the ordered stop and will signal this window.
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            ShowSendFeedback("管理プロセスへ終了・再起動を依頼できません。状態を確認してください。");
        }
        finally
        {
            _controlRequest = false;
            if (!_closing) RefreshComposer();
        }
    }

    internal void RequestLocalExit()
    {
        if (_closing || _localExitQueued)
            return;
        _localExitQueued = true;
        _exitRequested = true;
        StopReactions();
        _placementTimer.Stop();

        // Leave the active ContextMenu/Popup event stack before shutting down.
        // This is only the local WPF shutdown. The supervisor sends the stop
        // event and observes our exit before stopping its other children.
        if (_contextMenu is { IsOpen: true } menu)
            menu.IsOpen = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            PrepareShutdown();
            Application.Current.Shutdown();
        }));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
            PrepareShutdown();
    }

    private void PrepareShutdown()
    {
        if (_closing)
            return;

        _closing = true;
        StopReactions();
        _placementTimer.Stop();
        _panels.PrepareForShutdown();
        if (_contextMenu is { IsOpen: true } menu)
            menu.IsOpen = false;
        _lifetime.Cancel();

        // Preferences are best-effort at shutdown. A disappearing monitor/HWND
        // must not prevent the application from releasing its instance lock.
        if (_initialized)
        {
            try { RememberPosition(); }
            catch (InvalidOperationException error)
            {
                System.Diagnostics.Trace.TraceWarning("Desktop position could not be saved at shutdown: {0}",
                    error.GetType().Name);
            }
        }
        // WPF closes the owned windows as part of closing this owner. In the
        // explicit exit path Application.Shutdown also closes every app window.
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _relay.Dispose(); }
        finally { base.OnClosed(e); }
    }
}
