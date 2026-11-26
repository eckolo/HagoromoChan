using System.Globalization;
using HagoromoChan.Contracts;
using Microsoft.Data.Sqlite;

namespace HagoromoChan.Relay;

public sealed partial class ChatStore : IDisposable
{
    private const int MaximumTextLength = 32768;
    private const int MaximumPageSize = 100;
    private const string DeliveryFailureMessage = "メッセージの配信に失敗しました。再試行を待っています。";
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private bool disposed;

    public string ChannelId { get; }

    public ChatStore(string databasePath, string channelId = "hagoromo-mini")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ChannelId = channelId;

        if (databasePath != ":memory:")
        {
            databasePath = Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        }

        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());

        try
        {
            connection.Open();
            using var pragmas = connection.CreateCommand();
            pragmas.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
            pragmas.ExecuteNonQuery();
            InitializeSchema();
            InitializeCloudImports();
            InitializeWorkWake();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public ChatMessage AddUserMessage(SendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = ValidateRequestId(request.RequestId);

        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var existing = FindMessage(requestId, transaction);
            if (existing is not null)
            {
                if (existing.Text != request.Text || existing.InReplyTo != request.InReplyTo)
                    throw new ConflictException("同じ送信IDに異なる内容が指定されています。");
                transaction.Commit();
                return existing;
            }

            ValidateText(request.Text);

            if (request.InReplyTo is not null)
            {
                var parent = FindMessage(request.InReplyTo, transaction);
                if (parent?.Role != "assistant")
                    throw new ValidationException("返信先には、受信済みの回答を指定してください。");
            }

            var createdAt = DateTimeOffset.UtcNow;
            var message = InsertMessage(requestId, "user", request.Text, request.InReplyTo,
                requestId, createdAt, transaction);

            // Keep the destination after expiration or unsubscribe, so queued messages
            // cannot silently move to a different dot when a connection is replaced.
            var savedSubscription = ReadSavedSubscription(transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO outbox(event_id, request_id, subscription_id, next_attempt, created_at)
                VALUES ($eventId, $requestId, $subscriptionId, $createdAt, $createdAt);
                """;
            command.Parameters.AddWithValue("$eventId", "evt_" + requestId);
            command.Parameters.AddWithValue("$requestId", requestId);
            command.Parameters.AddWithValue("$subscriptionId", (object?)savedSubscription?.Subscription.Id ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", Timestamp(createdAt));
            command.ExecuteNonQuery();
            transaction.Commit();
            return message;
        }
    }

    public ChatMessage AddReply(string requestId, string text)
    {
        requestId = ValidateRequestId(requestId);

        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var request = FindMessage(requestId, transaction);
            if (request?.Role != "user")
                throw new KeyNotFoundException("指定された送信メッセージが見つかりません。");

            var replyId = "reply_" + requestId;
            var existing = FindMessage(replyId, transaction);
            if (existing is not null)
            {
                if (existing.Text != text)
                    throw new ConflictException("このメッセージには既に別の回答が届いています。");
                transaction.Commit();
                return existing;
            }

            ValidateText(text);

            var reply = InsertMessage(replyId, "assistant", text, requestId, requestId,
                DateTimeOffset.UtcNow, transaction);
            transaction.Commit();
            return reply;
        }
    }

    // A proactive assistant message has no corresponding user request. Its
    // internal correlation key is its own namespaced ID, never a user GUID.
    // Keep schema v2 and the existing reply/outbox invariants intact.
    public ChatMessage AddProactiveMessage(string messageId, string text)
    {
        if (!Guid.TryParseExact(messageId, "D", out var parsed) || parsed == Guid.Empty)
            throw new ValidationException("発言IDには有効なGUIDを指定してください。");
        ValidateText(text);
        var id = "proactive_" + parsed.ToString("D");

        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var existing = FindMessage(id, transaction);
            if (existing is not null)
            {
                if (existing.Role != "assistant" || existing.InReplyTo is not null ||
                    existing.Text != text)
                    throw new ConflictException("同じ発言IDに異なる内容が指定されています。");
                transaction.Commit();
                return existing;
            }

            // Do not insert an outbox entry or synthesize a user request.
            var message = InsertMessage(id, "assistant", text, null, id,
                DateTimeOffset.UtcNow, transaction);
            transaction.Commit();
            return message;
        }
    }

    public MessagePage ReadMessages(long after = 0, int limit = 100)
    {
        if (after < 0)
            throw new ValidationException("メッセージ位置には0以上の値を指定してください。");
        ValidateLimit(limit);

        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, id, role, text, in_reply_to, created_at
                FROM messages WHERE sequence > $after ORDER BY sequence LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$limit", limit + 1);
            using var reader = command.ExecuteReader();
            var messages = new List<ChatMessage>();
            while (reader.Read())
                messages.Add(ReadMessage(reader));

            var hasMore = messages.Count > limit;
            if (hasMore)
                messages.RemoveAt(messages.Count - 1);
            var nextCursor = messages.Count == 0 ? after : messages[^1].Sequence;
            return new MessagePage(messages, nextCursor, hasMore);
        }
    }

    public ChatMessage GetRequest(string requestId)
    {
        requestId = ValidateRequestId(requestId);
        lock (gate)
        {
            ThrowIfDisposed();
            var message = FindMessage(requestId);
            if (message?.Role != "user")
                throw new KeyNotFoundException("指定された送信メッセージが見つかりません。");
            return message;
        }
    }

    public IReadOnlyList<ChatMessage> GetContext(string requestId, int limit = 12)
    {
        ValidateLimit(limit);
        lock (gate)
        {
            var request = GetRequest(requestId);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, id, role, text, in_reply_to, created_at
                FROM messages WHERE sequence <= $sequence ORDER BY sequence DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$sequence", request.Sequence);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var messages = new List<ChatMessage>();
            while (reader.Read())
                messages.Add(ReadMessage(reader));
            messages.Reverse();
            return messages;
        }
    }

    public RelayStatus GetStatus()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var subscription = ReadSavedSubscription();
            var subscribed = subscription is { Active: true } &&
                subscription.Subscription.RefreshBefore > DateTimeOffset.UtcNow;
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN o.delivered = 0 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN o.delivered = 1 THEN 1 ELSE 0 END), 0)
                FROM outbox o
                WHERE NOT EXISTS (SELECT 1 FROM messages m
                    WHERE m.request_id = o.request_id AND m.role = 'assistant');
                """;
            int queued;
            int awaitingReply;
            using (var reader = command.ExecuteReader())
            {
                reader.Read();
                queued = reader.GetInt32(0);
                awaitingReply = reader.GetInt32(1);
            }

            command.CommandText = """
                SELECT last_error FROM outbox o
                WHERE delivered = 0 AND last_error IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM messages m
                    WHERE m.request_id = o.request_id AND m.role = 'assistant')
                ORDER BY created_at DESC LIMIT 1;
                """;
            var lastError = command.ExecuteScalar() as string;
            return new RelayStatus(ChannelId, subscribed, subscription?.Subscription.RefreshBefore,
                queued, awaitingReply, lastError);
        }
    }

    public Subscription? GetSubscription()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var saved = ReadSavedSubscription();
            return saved is { Active: true } ? saved.Subscription : null;
        }
    }

    public void SaveSubscription(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (string.IsNullOrWhiteSpace(subscription.Id))
            throw new ValidationException("購読IDが必要です。");
        if (!Uri.TryCreate(subscription.CallbackUrl, UriKind.Absolute, out var callback) ||
            callback.Scheme != Uri.UriSchemeHttps || callback.UserInfo.Length != 0 || callback.Fragment.Length != 0)
            throw new ValidationException("有効なHTTPSの通知先が必要です。");
        if (string.IsNullOrWhiteSpace(subscription.Secret))
            throw new ValidationException("通知用の署名情報が必要です。");

        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var saved = ReadSavedSubscription(transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*) FROM outbox o
                WHERE subscription_id IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM messages m
                    WHERE m.request_id = o.request_id AND m.role = 'assistant');
                """;
            var outstanding = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (outstanding > 0 && saved is not null &&
                (saved.Subscription.Id != subscription.Id || saved.Subscription.CallbackUrl != subscription.CallbackUrl))
                throw new ConflictException("未回答のメッセージがあります。元の接続で回答を受信してから接続先を変更してください。");

            command.CommandText = """
                INSERT INTO subscription(singleton, id, callback_url, secret, refresh_before, active)
                VALUES (1, $id, $callback, $secret, $refreshBefore, 1)
                ON CONFLICT(singleton) DO UPDATE SET
                    id = excluded.id, callback_url = excluded.callback_url, secret = excluded.secret,
                    refresh_before = excluded.refresh_before, active = 1;
                """;
            command.Parameters.AddWithValue("$id", subscription.Id);
            command.Parameters.AddWithValue("$callback", subscription.CallbackUrl);
            command.Parameters.AddWithValue("$secret", subscription.Secret);
            command.Parameters.AddWithValue("$refreshBefore", Timestamp(subscription.RefreshBefore));
            command.ExecuteNonQuery();

            if (saved is not null && saved.Subscription.Id == subscription.Id &&
                saved.Subscription.CallbackUrl == subscription.CallbackUrl)
            {
                // A response from a request signed before renewal cannot invalidate
                // the verified connection or delay delivery with the renewed key.
                command.CommandText = """
                    UPDATE outbox SET generation = generation + 1
                    WHERE subscription_id = $id AND delivered = 0
                      AND NOT EXISTS (SELECT 1 FROM messages m
                        WHERE m.request_id = outbox.request_id AND m.role = 'assistant');
                    """;
                command.ExecuteNonQuery();
            }

            command.CommandText = """
                UPDATE outbox SET subscription_id = $id WHERE subscription_id IS NULL;
                UPDATE outbox SET attempts = 0, next_attempt = $now, last_error = NULL, failure_code = NULL
                WHERE subscription_id = $id AND delivered = 0 AND failure_code = 'callback_expired'
                  AND NOT EXISTS (SELECT 1 FROM messages m
                    WHERE m.request_id = outbox.request_id AND m.role = 'assistant');
                """;
            command.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void Unsubscribe(string subscriptionId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE subscription SET active = 0 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", subscriptionId);
            command.ExecuteNonQuery();
        }
    }

    public PendingDelivery? GetNextDelivery(DateTimeOffset now)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var saved = ReadSavedSubscription();
            if (saved is not { Active: true } || saved.Subscription.RefreshBefore <= now)
                return null;

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT o.event_id, o.attempts, o.generation, m.sequence, m.id, m.role, m.text, m.in_reply_to, m.created_at
                FROM outbox o JOIN messages m ON m.id = o.request_id
                WHERE o.delivered = 0 AND o.subscription_id = $subscriptionId AND o.next_attempt <= $now
                  AND NOT EXISTS (SELECT 1 FROM messages reply
                    WHERE reply.request_id = o.request_id AND reply.role = 'assistant')
                ORDER BY m.sequence LIMIT 1;
                """;
            command.Parameters.AddWithValue("$subscriptionId", saved.Subscription.Id);
            command.Parameters.AddWithValue("$now", Timestamp(now));
            string eventId;
            int attempts;
            int generation;
            ChatMessage message;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    return null;
                eventId = reader.GetString(0);
                attempts = reader.GetInt32(1);
                generation = reader.GetInt32(2);
                message = ReadMessage(reader, 3);
            }

            return new PendingDelivery(eventId, message, saved.Subscription, attempts, GetContext(message.Id), generation);
        }
    }

    public void MarkDelivered(string eventId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE outbox SET delivered = 1, last_error = NULL, failure_code = NULL WHERE event_id = $eventId;";
            command.Parameters.AddWithValue("$eventId", eventId);
            if (command.ExecuteNonQuery() == 0)
                throw new KeyNotFoundException("配信待ちのメッセージが見つかりません。");
        }
    }

    public void MarkDeliveryFailed(string eventId, string error, DateTimeOffset nextAttempt,
        int? expectedGeneration = null)
    {
        var failureCode = error switch
        {
            "callback_expired" => "callback_expired",
            "payload_too_large" => "payload_too_large",
            _ => "delivery_failed"
        };
        var failureMessage = failureCode switch
        {
            "callback_expired" => "通知の接続期限が切れました。ChatGPT側で接続を更新してください。",
            "payload_too_large" => "送信内容が通知の上限を超えたため、このメッセージの配信を停止しました。",
            _ when nextAttempt == DateTimeOffset.MaxValue => "メッセージの配信を停止しました。接続を確認して再試行してください。",
            _ => DeliveryFailureMessage
        };
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox SET attempts = attempts + 1, next_attempt = $nextAttempt,
                    last_error = $error, failure_code = $failureCode
                WHERE event_id = $eventId AND delivered = 0
                  AND ($expectedGeneration IS NULL OR generation = $expectedGeneration)
                  AND NOT EXISTS (SELECT 1 FROM messages m
                    WHERE m.request_id = outbox.request_id AND m.role = 'assistant');
                """;
            command.Parameters.AddWithValue("$eventId", eventId);
            command.Parameters.AddWithValue("$expectedGeneration", (object?)expectedGeneration ?? DBNull.Value);
            command.Parameters.AddWithValue("$nextAttempt", Timestamp(nextAttempt));
            command.Parameters.AddWithValue("$failureCode", failureCode);
            // Network errors may contain callback URLs or credentials. Persist only a fixed status.
            command.Parameters.AddWithValue("$error", failureMessage);
            command.ExecuteNonQuery();
        }
    }

    public int RetryFailedDeliveries()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var saved = ReadSavedSubscription();
            var now = DateTimeOffset.UtcNow;
            if (saved is not { Active: true } || saved.Subscription.RefreshBefore <= now)
                return 0;

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox SET attempts = 0, next_attempt = $now, last_error = NULL, failure_code = NULL,
                    generation = generation + 1
                WHERE subscription_id = $subscriptionId AND delivered = 0 AND failure_code = 'delivery_failed'
                  AND NOT EXISTS (SELECT 1 FROM messages m
                    WHERE m.request_id = outbox.request_id AND m.role = 'assistant');
                """;
            command.Parameters.AddWithValue("$subscriptionId", saved.Subscription.Id);
            command.Parameters.AddWithValue("$now", Timestamp(now));
            return command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            connection.Dispose();
        }
    }

    private void InitializeSchema()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 2)
            throw new InvalidOperationException("このデータベースには新しいバージョンのアプリが必要です。");

        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                channel_id TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                role TEXT NOT NULL CHECK (role IN ('user', 'assistant')),
                text TEXT NOT NULL,
                in_reply_to TEXT REFERENCES messages(id),
                request_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE (request_id, role)
            );
            CREATE TABLE IF NOT EXISTS outbox (
                event_id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL UNIQUE REFERENCES messages(id),
                subscription_id TEXT,
                delivered INTEGER NOT NULL DEFAULT 0 CHECK (delivered IN (0, 1)),
                attempts INTEGER NOT NULL DEFAULT 0,
                generation INTEGER NOT NULL DEFAULT 0,
                next_attempt TEXT NOT NULL,
                created_at TEXT NOT NULL,
                last_error TEXT,
                failure_code TEXT
            );
            CREATE TABLE IF NOT EXISTS subscription (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                id TEXT NOT NULL,
                callback_url TEXT NOT NULL,
                secret TEXT NOT NULL,
                refresh_before TEXT NOT NULL,
                active INTEGER NOT NULL CHECK (active IN (0, 1))
            );
            CREATE INDEX IF NOT EXISTS outbox_pending ON outbox(delivered, next_attempt);
            INSERT INTO metadata(singleton, channel_id) VALUES (1, $channelId)
                ON CONFLICT(singleton) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$channelId", ChannelId);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT channel_id FROM metadata WHERE singleton = 1;";
        if ((string?)command.ExecuteScalar() != ChannelId)
            throw new ConflictException("このデータベースは別のチャンネルに割り当てられています。");

        // Existing v1 databases keep their messages, delivery state and identities.
        // The column check also makes an interrupted or previously upgraded open safe.
        command.CommandText = "PRAGMA table_info(outbox);";
        var hasGeneration = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                hasGeneration |= reader.GetString(1) == "generation";
        }
        if (!hasGeneration)
        {
            command.CommandText = "ALTER TABLE outbox ADD COLUMN generation INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA user_version = 2;";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private ChatMessage InsertMessage(string id, string role, string text, string? inReplyTo,
        string requestId, DateTimeOffset createdAt, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages(id, role, text, in_reply_to, request_id, created_at)
            VALUES ($id, $role, $text, $inReplyTo, $requestId, $createdAt)
            RETURNING sequence;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$inReplyTo", (object?)inReplyTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$createdAt", Timestamp(createdAt));
        var sequence = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new ChatMessage(sequence, id, role, text, inReplyTo, createdAt);
    }

    private ChatMessage? FindMessage(string id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, id, role, text, in_reply_to, created_at FROM messages WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMessage(reader) : null;
    }

    private SavedSubscription? ReadSavedSubscription(SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, callback_url, secret, refresh_before, active FROM subscription WHERE singleton = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new SavedSubscription(new Subscription(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            ParseTimestamp(reader.GetString(3))), reader.GetBoolean(4));
    }

    private static ChatMessage ReadMessage(SqliteDataReader reader, int offset = 0) =>
        new(reader.GetInt64(offset), reader.GetString(offset + 1), reader.GetString(offset + 2),
            reader.GetString(offset + 3), reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
            ParseTimestamp(reader.GetString(offset + 5)));

    private static string ValidateRequestId(string requestId)
    {
        if (!Guid.TryParseExact(requestId, "D", out var parsed) || parsed == Guid.Empty)
            throw new ValidationException("送信IDには有効なGUIDを指定してください。");
        return parsed.ToString("D");
    }

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            throw new ValidationException($"本文には1文字以上{MaximumTextLength}文字以下のテキストを指定してください。");
    }

    private static void ValidateLimit(int limit)
    {
        if (limit < 1 || limit > MaximumPageSize)
            throw new ValidationException($"取得件数には1以上{MaximumPageSize}以下の値を指定してください。");
    }

    private static string Timestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed record SavedSubscription(Subscription Subscription, bool Active);
}
