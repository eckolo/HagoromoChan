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
                _connectionProblem = true;
                ConnectionText.Text = "ローカル中継に接続できません";
                RelayRetryButton.Visibility = Visibility.Collapsed;
                QueueText.Text = "送信・返信状態は未確認";
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
        var firstCompleteRead = !_historyLoaded;
        var scrollToEnd = firstCompleteRead || IsAtBottom();
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
        ObserveReplies(firstCompleteRead);
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
                // Read status after local acceptance; never report that Dot already replied.
                // The regular polling loop remains responsible for authoritative queue counts.
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
        if (_initialized)
        {
            _panels.Arrange();
            RefreshStatus();
        }
    }

    private void ShowStatus(RelayStatus status)
    {
        _status = status;
        _connectionProblem = false;
        ConnectionText.Text = status.Subscribed ? "購読有効（ローカル中継）" : "羽衣ちゃんの購読待ち";
        ConnectionIndicator.Fill = new SolidColorBrush(status.Subscribed
            ? Color.FromRgb(168, 217, 198)
            : Color.FromRgb(217, 187, 122));
        var pendingCount = status.QueuedCount + status.AwaitingReplyCount;
        QueueText.Text = pendingCount > 0
            ? $"送信待ち {status.QueuedCount} 件　返信待ち {status.AwaitingReplyCount} 件"
            : status.Subscribed ? "" : "中継は起動しています";
        ShowConnectionError(status.LastError);
        RelayRetryButton.Visibility = status.QueuedCount > 0 && !string.IsNullOrWhiteSpace(status.LastError)
            ? Visibility.Visible : Visibility.Collapsed;
        RelayRetryButton.IsEnabled = !_relayRetrying;
        RefreshStatus();
    }

    private void ShowConnectionError(string? message)
    {
        ConnectionErrorText.Text = message;
        ConnectionErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        RefreshStatus();
    }

    private void ShowSendFeedback(string? message)
    {
        SendFeedbackText.Text = message;
        SendFeedbackText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        RefreshStatus();
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
            if (_imeComposing || _compositionJustEnded)
                return; // Let the IME handle Escape before hiding the composer.
            e.Handled = true;
            _panels.SetVisible(_panels.Composer, false);
            return;
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
        RefreshComposer();
    }

    private void NewMessagesButton_Click(object sender, RoutedEventArgs e) => ScrollToLatest();

    private ChatMessage[] Answers() => _messages.Where(item => item.Message.Role == "assistant")
        .Select(item => item.Message).ToArray();

    private void ObserveReplies(bool firstCompleteRead)
    {
        var priorUnread = new HashSet<string>(_preferences.UnreadIds, StringComparer.Ordinal);
        var changed = _preferences.Observe(_messages.Select(item => item.Message).ToArray(), _cursor);
        if (changed)
            SavePreferences();
        var newUnread = _preferences.UnreadIds.Any(id => !priorUnread.Contains(id));
        if ((firstCompleteRead || newUnread) && !_panels.Wants(_panels.Bubble))
        {
            var firstUnread = Answers().FirstOrDefault(m => _preferences.UnreadIds.Contains(m.Id));
            if (firstUnread is not null)
                ShowReply(firstUnread.Id);
        }
        RefreshReply();
    }

    private void ShowReply(string id)
    {
        _replyId = id;
        RefreshReply();
        _panels.SetVisible(_panels.Bubble, true);
        RefreshStatus();
    }

    private void MoveReply(int offset)
    {
        var answers = Answers();
        var index = Array.FindIndex(answers, m => m.Id == _replyId);
        if (index < 0 || index + offset < 0 || index + offset >= answers.Length)
            return;
        ShowReply(answers[index + offset].Id);
    }

    private void OpenUnread()
    {
        var answers = Answers();
        var answer = answers.FirstOrDefault(m => _preferences.UnreadIds.Contains(m.Id)) ?? answers.LastOrDefault();
        if (answer is not null)
        {
            // On a narrow monitor a history panel may temporarily cover the conversation.
            _panels.SetVisible(_panels.History, false);
            ShowReply(answer.Id);
        }
    }

    private void CloseReply()
    {
        if (_replyId is string id && _preferences.UnreadIds.Remove(id))
            SavePreferences();
        _replyId = null;
        _panels.SetVisible(_panels.Bubble, false);
        RefreshReply();
    }

    private void RefreshReply()
    {
        var answers = Answers();
        var index = Array.FindIndex(answers, m => m.Id == _replyId);
        var answer = index < 0 ? null : answers[index];
        if (answer is null && _panels.Wants(_panels.Bubble))
            _panels.SetVisible(_panels.Bubble, false);
        _panels.UpdateReply(answer is null ? null : $"羽衣ちゃん　{answer.CreatedAt.LocalDateTime:M/d HH:mm}",
            answer?.Text, index, answers.Length, _preferences.UnreadIds.Count);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (!_initialized || _closing)
            return;
        var warnings = new[] { _assetWarning, _preferencesWarning,
            !_panels.Composer.IsVisible ? SendFeedbackText.Text : null };
        var details = string.Join(Environment.NewLine, warnings.Where(s => !string.IsNullOrWhiteSpace(s)));
        _panels.StatusDetail.Text = details;
        _panels.StatusDetail.Visibility = details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _panels.OpenInputButton.Visibility = !string.IsNullOrWhiteSpace(SendFeedbackText.Text)
            && !_panels.Composer.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        var unread = _preferences.UnreadIds.Count;
        _panels.UnreadButton.Content = $"新着 {unread}";
        _panels.UnreadButton.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        var waiting = _status is { } status && (status.QueuedCount > 0 || status.AwaitingReplyCount > 0);
        var shouldShow = _connectionProblem || _status is { Subscribed: false } || waiting || _sending
            || _preferencesSaveBlocked || details.Length != 0 || !string.IsNullOrEmpty(ConnectionErrorText.Text)
            || (unread > 0 && !_panels.Bubble.IsVisible);
        if (_sending && string.IsNullOrEmpty(QueueText.Text))
            QueueText.Text = "送信受付を確認中…";
        else if (!_sending && QueueText.Text == "送信受付を確認中…")
            QueueText.Text = "";
        _panels.SetVisible(_panels.Status, shouldShow);
        if (shouldShow)
            _panels.Arrange();
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
