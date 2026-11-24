using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow(string dataDirectory)
    {
        InitializeComponent();
        _relay = new RelayClient(dataDirectory);
        _pendingStore = new PendingMessageStore(dataDirectory);
        HistoryItems.ItemsSource = _messages;

        TextCompositionManager.AddPreviewTextInputStartHandler(MessageInput, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(MessageInput, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(MessageInput, (_, _) => EndComposition());
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RestorePendingAsync();
            MessageInput.Focus();
            await PollAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RestorePendingAsync()
    {
        try
        {
            _pending = await _pendingStore.LoadAsync(_lifetime.Token);
            _storageBlocked = false;
            if (_pending is not null)
            {
                MessageInput.Text = _pending.Text;
                ShowSendFeedback("前回の送信結果を確認しています。未受理の場合は再試行できます。");
            }
            else
            {
                ShowSendFeedback(null);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _storageBlocked = true;
            ShowSendFeedback("未確認のメッセージを読み込めません。「読込を再試行」で再確認してください。" +
                Environment.NewLine + error.Message);
        }

        RefreshComposer();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _relayGate.WaitAsync(cancellationToken);
                try
                {
                    var status = await _relay.GetStatusAsync(cancellationToken);
                    ShowStatus(status);
                    await ReadMessagesAsync(cancellationToken);
                }
                finally
                {
                    _relayGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (IsRecoverable(error))
            {
                ConnectionText.Text = "接続待ち";
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(217, 187, 122));
                ShowConnectionError(DescribeError(error));
                if (!_historyLoaded)
                {
                    EmptyHistoryText.Text = "中継との接続を待っています。";
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        var scrollToEnd = !_historyLoaded || IsAtBottom();
        var addedAssistantMessage = false;
        MessagePage page;
        do
        {
            page = await _relay.GetMessagesAsync(_cursor, cancellationToken);
            if (page.Messages is null || page.NextCursor < _cursor
                || (page.HasMore && page.NextCursor <= _cursor))
            {
                throw new InvalidDataException("会話の続きの位置を読み取れません。");
            }

            foreach (var message in page.Messages)
            {
                var added = AddMessage(message);
                addedAssistantMessage |= added && message.Role == "assistant";
                if (MatchesPending(message))
                {
                    _pendingAccepted = true;
                }
            }

            // Only a complete history page advances the cursor. A POST result may arrive out of order.
            _cursor = page.NextCursor;
        }
        while (page.HasMore);

        _historyLoaded = true;
        EmptyHistoryText.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHistoryText.Text = "羽衣ちゃんへのメッセージを入力してください。";

        if (_pendingAccepted && !_sending)
        {
            FinishAcceptedMessage();
        }

        if (scrollToEnd)
        {
            ScrollToLatest();
        }
        else if (addedAssistantMessage)
        {
            NewMessagesButton.Visibility = Visibility.Visible;
        }

        RefreshComposer();
    }

    private bool AddMessage(ChatMessage message)
    {
        if (!_seenSequences.Add(message.Sequence))
        {
            return false;
        }

        var index = 0;
        while (index < _messages.Count && _messages[index].Message.Sequence < message.Sequence)
        {
            index++;
        }

        _messages.Insert(index, new MessageDisplay(message));
        EmptyHistoryText.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool MatchesPending(ChatMessage message) =>
        _pending is not null && message.Role == "user"
        && string.Equals(message.Id, _pending.RequestId, StringComparison.OrdinalIgnoreCase)
        && message.Text == _pending.Text && message.InReplyTo == _pending.InReplyTo;

    private async Task SendMessageAsync()
    {
        if (_sending || _imeComposing || _compositionJustEnded)
        {
            return;
        }

        if (_storageBlocked)
        {
            _sending = true;
            RefreshComposer();
            try
            {
                await RestorePendingAsync();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                _sending = false;
                RefreshComposer();
            }

            return;
        }

        if (_pending is null && (!_historyLoaded || string.IsNullOrWhiteSpace(MessageInput.Text)))
        {
            return;
        }

        _sending = true;
        RefreshComposer();
        try
        {
            if (_pendingAccepted)
            {
                FinishAcceptedMessage();
                return;
            }

            if (_pending is null)
            {
                var lastAssistant = _messages.LastOrDefault(item => item.Message.Role == "assistant");
                var request = new SendMessageRequest(Guid.NewGuid().ToString("D"), MessageInput.Text,
                    lastAssistant?.Message.Id);

                // Persist the request ID and exact text before any network side effect.
                await _pendingStore.SaveAsync(request, _lifetime.Token);
                _pending = request;
            }

            await _relayGate.WaitAsync(_lifetime.Token);
            try
            {
                if (!_pendingAccepted)
                {
                    var accepted = await _relay.SendMessageAsync(_pending, _lifetime.Token);
                    if (!MatchesPending(accepted))
                    {
                        throw new InvalidDataException("送信した内容と中継の受付結果が一致しません。");
                    }

                    AddMessage(accepted);
                    _pendingAccepted = true;
                }

                FinishAcceptedMessage();
                ScrollToLatest();
            }
            finally
            {
                _relayGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            var guidance = _pending is null
                ? "送信内容を保存できませんでした。入力を残したまま、もう一度送信できます。"
                : "送信結果を確認できません。「再試行」で同じメッセージの受付を確認します。";
            ShowSendFeedback(guidance + Environment.NewLine + DescribeError(error));
        }
        finally
        {
            _sending = false;
            RefreshComposer();
        }
    }

    private void FinishAcceptedMessage()
    {
        try
        {
            _pendingStore.Clear();
            _pending = null;
            _pendingAccepted = false;
            MessageInput.Clear();
            ShowSendFeedback(null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowSendFeedback("送信は受理されました。確認済みデータの整理を再試行してください。" +
                Environment.NewLine + error.Message);
        }
    }

    private void RefreshComposer()
    {
        MessageInput.IsReadOnly = _pending is not null || _sending || _storageBlocked;
        SendButton.Content = "送信";
        if (_pending is not null)
        {
            SendButton.Content = _pendingAccepted ? "確定を再試行" : "再試行";
        }

        if (_storageBlocked)
        {
            SendButton.Content = "読込を再試行";
        }

        if (_sending)
        {
            SendButton.Content = "確認中…";
        }

        SendButton.IsEnabled = !_sending && (_storageBlocked || _pending is not null
            || (_historyLoaded && !string.IsNullOrWhiteSpace(MessageInput.Text)));
    }

    private void ShowStatus(RelayStatus status)
    {
        ConnectionText.Text = status.Subscribed ? "羽衣ちゃんに接続中" : "羽衣ちゃんの接続待ち";
        ConnectionIndicator.Fill = new SolidColorBrush(status.Subscribed
            ? Color.FromRgb(168, 217, 198)
            : Color.FromRgb(217, 187, 122));
        var pendingCount = status.QueuedCount + status.AwaitingReplyCount;
        QueueText.Text = pendingCount > 0
            ? $"送信待ち {status.QueuedCount} 件　返信待ち {status.AwaitingReplyCount} 件"
            : status.Subscribed ? "メッセージを受信できます" : "中継は起動しています";
        ShowConnectionError(status.LastError);
        RelayRetryButton.Visibility = status.QueuedCount > 0 && !string.IsNullOrWhiteSpace(status.LastError)
            ? Visibility.Visible : Visibility.Collapsed;
        RelayRetryButton.IsEnabled = !_relayRetrying;
    }

    private void ShowConnectionError(string? message)
    {
        ConnectionErrorText.Text = message;
        ConnectionErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowSendFeedback(string? message)
    {
        SendFeedbackText.Text = message;
        SendFeedbackText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsRecoverable(Exception error) =>
        error is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or OperationCanceledException;

    private static string DescribeError(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "中継が起動しているか確認してください。",
        OperationCanceledException => "応答を待つ時間を超えました。接続は自動で再確認します。",
        HttpRequestException { StatusCode: null } => "中継に接続できません。起動スクリプトを実行してください。",
        _ => error.Message
    };

    private bool IsAtBottom() => HistoryScroll.ScrollableHeight - HistoryScroll.VerticalOffset <= 32;

    private void ScrollToLatest()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => HistoryScroll.ScrollToEnd());
        NewMessagesButton.Visibility = Visibility.Collapsed;
    }

    private void EndComposition()
    {
        _imeComposing = false;
        _compositionJustEnded = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => _compositionJustEnded = false);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async void RelayRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_relayRetrying)
        {
            return;
        }

        _relayRetrying = true;
        RelayRetryButton.IsEnabled = false;
        try
        {
            await _relayGate.WaitAsync(_lifetime.Token);
            try
            {
                var status = await _relay.RetryDeliveryAsync(_lifetime.Token);
                ShowStatus(status);
            }
            finally
            {
                _relayGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            ShowConnectionError("配信を再試行できませんでした。" + Environment.NewLine + DescribeError(error));
        }
        finally
        {
            _relayRetrying = false;
            RelayRetryButton.IsEnabled = true;
        }
    }

    private async void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndComposition();
        }

        if (e.Key != Key.Return || Keyboard.Modifiers != ModifierKeys.Control
            || _imeComposing || _compositionJustEnded)
        {
            return;
        }

        e.Handled = true;
        await SendMessageAsync();
    }

    private void MessageInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _imeComposing = false;

    private void MessageInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SendButton is not null)
        {
            RefreshComposer();
        }
    }

    private void NewMessagesButton_Click(object sender, RoutedEventArgs e) => ScrollToLatest();

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _relay.Dispose();
        base.OnClosed(e);
    }

    private sealed class MessageDisplay(ChatMessage message)
    {
        public ChatMessage Message { get; } = message;
        public string Text => Message.Text;
        public string Heading => $"{RoleName}　{Message.CreatedAt.LocalDateTime:M/d HH:mm}";
        private string RoleName => Message.Role switch
        {
            "assistant" => "羽衣ちゃん",
            "user" => "あなた",
            _ => "お知らせ"
        };
        public Brush Background { get; } = new SolidColorBrush(message.Role == "assistant"
            ? Color.FromRgb(34, 43, 55)
            : Color.FromRgb(39, 58, 65));
    }
}
