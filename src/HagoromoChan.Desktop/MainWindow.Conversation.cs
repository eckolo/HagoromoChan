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
                _panels.SetDraftText(_pending.Text);
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
            ReadRuntimeSnapshot();
            RefreshStatus();
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
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        var firstCompleteRead = !_historyLoaded;
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
                AddMessage(message);
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

        if (_pendingAccepted && !_sending)
        {
            FinishAcceptedMessage();
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
        return true;
    }

    private bool MatchesPending(ChatMessage message) =>
        _pending is not null && message.Role == "user"
        && string.Equals(message.Id, _pending.RequestId, StringComparison.OrdinalIgnoreCase)
        && message.Text == _pending.Text && message.InReplyTo == _pending.InReplyTo;

    private async Task SendMessageAsync()
    {
        if (_sending || _imeComposing || _compositionJustEnded || _closing || _exitRequested || _controlRequest)
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

        if (_pending is null && (!_historyLoaded || string.IsNullOrWhiteSpace(_panels.DraftText)))
        {
            return;
        }

        if (_pending is null && _panels.DraftText.Length > 32768)
        {
            ShowSendFeedback("送信できる文字数は32768文字までです。下書きを調整してください。");
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
                var request = new SendMessageRequest(Guid.NewGuid().ToString("D"), _panels.DraftText,
                    lastAssistant?.Message.Id);

                // Persist the request ID and exact text before any network side effect.
                await _pendingStore.SaveAsync(request, _lifetime.Token);
                _pending = request;
                _reactionRequestId = request.RequestId;
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

                NotifyLocalAcceptance();
                FinishAcceptedMessage();
                RefreshHistory();
                _panels.SelectLatest();
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
            if (_pending is null || error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict })
                _reactions.Fail(Environment.TickCount64);
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
            _panels.SetDraftText("");
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
        SendButton.Content = "➤";
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

        var sendDescription = _sending ? "送信受付を確認中"
            : _storageBlocked ? "未確認メッセージの読込を再試行"
            : _pendingAccepted ? "受付済みデータの整理を再試行"
            : _pending is not null ? "同じメッセージの送信受付を再確認"
            : "送信（Ctrl+Enter）、Enterで改行";
        SendButton.ToolTip = sendDescription;
        System.Windows.Automation.AutomationProperties.SetName(SendButton, sendDescription);
        SendButton.IsEnabled = !_closing && !_exitRequested && !_controlRequest && !_sending && (_storageBlocked || _pending is not null
            || (_historyLoaded && !string.IsNullOrWhiteSpace(_panels.DraftText)));
        if (_initialized)
        {
            _panels.RequestArrange();
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

    private void BeginComposition()
    {
        _imeComposing = true;
        _panels.SetComposing(true);
    }

    private void EndComposition()
    {
        _imeComposing = false;
        _compositionJustEnded = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _compositionJustEnded = false;
            if (!_imeComposing)
                _panels.SetComposing(false);
        }));
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
            ChangeConversationByUser(_panels.CloseComposer);
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

    private void MessageInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _imeComposing = false;
        _panels.SetComposing(false);
    }

    private void RefreshHistory()
    {
        _panels.UpdateHistory(_messages.Select(item => item.Message).ToArray(), _preferences.UnreadIds);
        // The first complete read reconciles the persisted checkpoint before the pose follows it.
        if (_historyLoaded)
            _reactions.SetUnread(_preferences.UnreadIds.Count > 0, Environment.TickCount64);
    }

    private void ObserveReplies(bool firstCompleteRead)
    {
        var priorUnread = new HashSet<string>(_preferences.UnreadIds, StringComparer.Ordinal);
        var snapshot = _messages.Select(item => item.Message).ToArray();
        var changed = _preferences.Observe(snapshot, _cursor);
        NotifyLocalAcceptance();
        _reactions.Receive(_reactionInbox.ObserveCompletedRead(snapshot, _cursor), Environment.TickCount64);
        var newUnread = _preferences.UnreadIds.Any(id => !priorUnread.Contains(id));
        RefreshHistory();
        if (changed)
            SavePreferences();

        // Explicit dismissal suppresses status and existing unread notifications.
        // Only a genuinely new reply is allowed to reopen the conversation.
        if ((newUnread || (firstCompleteRead && !_compactSuppressed)) && !_panels.HistoryOpen)
        {
            var answer = _messages.Select(item => item.Message).FirstOrDefault(message =>
                message.Role == "assistant" && _preferences.UnreadIds.Contains(message.Id)
                && (!newUnread || !priorUnread.Contains(message.Id)));
            if (answer is not null)
            {
                _compactSuppressed = false;
                _panels.ShowMessage(answer.Id);
            }
        }
        RefreshStatus();
    }

    private void OpenUnread()
    {
        _compactSuppressed = false;
        var messages = _messages.Select(item => item.Message);
        var answer = messages.FirstOrDefault(message => _preferences.UnreadIds.Contains(message.Id))
            ?? messages.LastOrDefault();
        if (answer is not null)
            ChangeConversationByUser(() => _panels.OpenUnreadMessage(answer.Id));
        RefreshStatus();
    }

    private void MarkRead(IReadOnlyList<string> ids)
    {
        var changed = false;
        foreach (var id in ids)
            changed |= _preferences.UnreadIds.Remove(id);
        if (!changed)
            return;
        SavePreferences();
        RefreshHistory();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (!_initialized || _closing)
            return;
        if (_compactSuppressed && !_expandedStatus)
        {
            _panels.SetVisible(_panels.Status, false);
            return;
        }

        var warnings = new[] { _assetWarning, _preferencesWarning,
            SendFeedbackText.Text };
        var details = string.Join(Environment.NewLine, warnings.Where(s => !string.IsNullOrWhiteSpace(s)));
        _panels.StatusDetail.Text = details;
        _panels.StatusDetail.Visibility = details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _panels.OpenInputButton.Visibility = !string.IsNullOrWhiteSpace(SendFeedbackText.Text)
            && !_panels.ComposerOpen ? Visibility.Visible : Visibility.Collapsed;
        var unread = _preferences.UnreadIds.Count;
        _panels.UnreadButton.Content = $"新着 {unread}";
        _panels.UnreadButton.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        var waiting = _status is { } status && (status.QueuedCount > 0 || status.AwaitingReplyCount > 0);
        var summary = string.IsNullOrEmpty(_managedSession) ? ""
            : _runtime is null ? "管理状態: 未確認"
            : !_runtime.RelayReady ? "中継: 未準備"
            : !_runtime.TunnelReady ? "トンネル: 未準備／停止"
            : "";
        _panels.SetRuntimeDetails(_expandedStatus, DescribeRuntime(), summary);
        var runtimeProblem = _managedSession is not null &&
            (_runtime is null || !_runtime.RelayReady || !_runtime.TunnelReady);
        var shouldShow = _expandedStatus || runtimeProblem || _connectionProblem || _status is { Subscribed: false } || waiting || _sending
            || _preferencesSaveBlocked || details.Length != 0 || !string.IsNullOrEmpty(ConnectionErrorText.Text)
            || unread > 0;
        if (_sending && string.IsNullOrEmpty(QueueText.Text))
            QueueText.Text = "送信受付を確認中…";
        else if (!_sending && QueueText.Text == "送信受付を確認中…")
            QueueText.Text = "";
        _panels.SetVisible(_panels.Status, shouldShow);
        if (shouldShow)
            _panels.RequestArrange();
    }

    private void ToggleConnectionDetails()
    {
        _expandedStatus = !_expandedStatus;
        _compactSuppressed = false;
        ReadRuntimeSnapshot();
        RefreshStatus();
    }

    private void ReadRuntimeSnapshot()
    {
        _runtime = null;
        if (string.IsNullOrEmpty(_managedSession)) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<RuntimeSnapshot>(
                File.ReadAllText(System.IO.Path.Combine(_runtimeDirectory, RuntimeControl.StatusFile)));
            if (RuntimeControl.IsCurrentSnapshot(snapshot, _managedSession, DateTimeOffset.UtcNow))
                _runtime = snapshot;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private string DescribeRuntime()
    {
        if (string.IsNullOrEmpty(_managedSession))
            return "個別開発起動（管理対象外）\nRelay・トンネルのプロセス状態は未確認";
        if (_runtime is not { } state)
            return "管理プロセスの状態を確認中";
        var tunnel = state.TunnelState switch
        {
            "ready" => "準備完了",
            "starting" or "not-ready" => "接続準備中／未準備",
            "unconfigured" => "未設定",
            "blocked" => "重複起動のため停止",
            "failed" or "exited" => "異常終了／起動失敗",
            _ => "停止"
        };
        var phase = state.Phase switch
        {
            "starting" => "起動中", "stopping" => "終了中", "running" => "稼働中",
            _ => "停止／不明"
        };
        var subscription = _status is null || _connectionProblem ? "未確認"
            : _status.Subscribed ? "ローカル上有効" : "未購読／期限切れ";
        return $"管理: {phase}\nDesktop: PID {state.DesktopPid?.ToString() ?? "不明"}\n"
            + $"Relay: {(state.RelayReady ? "応答あり" : "未準備")} (PID {state.RelayPid?.ToString() ?? "なし"})\n"
            + $"Tunnel: {tunnel} (PID {state.TunnelPid?.ToString() ?? "なし"})\n"
            + $"購読: {subscription}\nクラウド側の購読・Dot実行状態は別途確認"
            + (string.IsNullOrWhiteSpace(state.Problem) ? "" : "\n" + state.Problem);
    }

    private sealed class MessageDisplay(ChatMessage message)
    {
        public ChatMessage Message { get; } = message;
    }
}
