using HagoromoChan.Contracts;
using HagoromoChan.Relay;
using Microsoft.Data.Sqlite;

var tests = new (string Name, Action Run)[]
{
    ("M05 imports preserve Dot isolation, exact retries and source identity", CloudImportChecks.Run),
    ("Messages and channel binding persist across a restart", Persistence),
    ("Identical sends and replies are idempotent, conflicting retries fail", Idempotency),
    ("Invalid messages and reply destinations are rejected atomically", Validation),
    ("Paged cursors preserve every message without repeating it", Pagination),
    ("Retry timing, attempts and event identity survive restart", DurableRetries),
    ("Pre-subscription messages wait, then follow the first subscription", QueuedBeforeSubscription),
    ("Expired and revoked subscriptions stop delivery", ExpiredSubscription),
    ("Pending messages cannot be redirected to another subscriber", SubscriptionBinding),
    ("A reply racing webhook receipt never regresses or gets sent again", ReplyBeforeReceipt),
    ("Context includes the request and only messages known at its creation", ContextSnapshot),
    ("Manual retry resets transient failures while preserving terminal failures", ManualRetries),
    ("A late failure cannot undo manual retry, including after restart", LateFailureAfterManualRetry),
    ("A late failure using an old key cannot undo verified renewal", LateFailureAfterRenewal),
    ("Schema v1 upgrades preserve messages and pending delivery state", GenerationMigration),
    ("Concurrent retries commit one user message and one answer", ConcurrentRetries),
    ("MCP protocol, signatures, secure callbacks and two-turn conversation", ProtocolChecks.RunAll),
    ("Standalone messages persist without changing request or subscription identity", ProactiveChecks.StorePersistence),
    ("Standalone input validation and concurrent retries are safe", ProactiveChecks.ValidityAndConcurrentRetry),
    ("MCP standalone message tool is isolated from existing replies", ProactiveChecks.McpTool),
    ("Standalone messages share unread persistence without duplicate notifications", ProactiveChecks.DesktopUnread),
    ("Initial history, partial reads and duplicate replies do not replay reactions", CharacterReactionTests.Inbox),
    ("Character reactions preserve priority, interaction suspension and expiration", CharacterReactionTests.Playback),
    ("Opening and dismissing conversation use distinct gestures and respect priority", CharacterReactionTests.ConversationGestures),
    ("Only a short closing click preserves a still-live message reaction", CharacterReactionTests.CloseDuringMessage),
    ("Unread rest follows reconciled IDs, preserves its phase and yields to active reactions", CharacterReactionTests.Unread),
    ("Waiting uses a thinking loop, follows activity and resolves only matching replies", CharacterReactionTests.Waiting),
    ("System, app-only and disabled motion preferences preserve prior choices", CharacterReactionTests.MotionPreferences),
    ("Nearby cursors hold the gaze across distance and DPI boundaries", CursorGazeTests.Proximity),
    ("Panel input immediately redirects the gaze and repeated interaction extends it", CursorGazeTests.PanelActivity),
    ("Desktop overlay placement and work-area bounds", DesktopUiChecks.Placement),
    ("Desktop unread checkpoints and preferences survive restart", DesktopUiChecks.Preferences),
    ("Desktop checkpoint detects a replaced history and ignores unconfirmed pages", DesktopUiChecks.CheckpointIdentity),
    ("Conversation text pages preserve Japanese, Unicode, newlines and long unbroken text", ConversationUiChecks.LosslessPages),
    ("Conversation stack expands upward and contracts without dropping stored messages", ConversationUiChecks.StackNavigation),
    ("Conversation zone is independent of content and mascot never upscales", ConversationUiChecks.FixedZoneAndSourceSize),
    ("Display preference migration makes the former large size standard", ConversationUiChecks.PreferencesMigration),
    ("Input and conversation history toggle independently", ConversationUiChecks.IndependentVisibility),
    ("History navigation retains surviving cards", ConversationUiChecks.RetainedHistoryCards),
    ("Auxiliary surfaces remain adjacent to the mascot", ConversationUiChecks.NearbyAuxiliaryWindows),
    ("Status width follows mascot size without obscuring open conversation", ConversationUiChecks.MascotStatusWidth),
    ("Runtime snapshots are scoped to the current session and time", RuntimeControlChecks.SnapshotIdentity),
    ("Current-user runtime control pipe transmits bounded commands and acknowledgements", RuntimeControlChecks.PipeCommands),
    ("Windows argument quoting handles spaces, Japanese paths, quotes and trailing separators", RuntimeControlChecks.WindowsArgumentQuoting),
    ("Tunnel stdio command preserves Windows paths through the inner parser", RuntimeControlChecks.TunnelStdioCommandQuoting)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}\n{exception}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} regression tests passed.");
Environment.ExitCode = failures == 0 ? 0 : 1;

static SendMessageRequest Request(string text = "こんにちは", string? inReplyTo = null) =>
    new(Guid.NewGuid().ToString("D"), text, inReplyTo);

static Subscription SubscriptionFor(string id = "sub_test", DateTimeOffset? expiration = null) =>
    new(id, "https://callback.example.test/events/" + id,
        "whsec_" + Convert.ToBase64String(new byte[32]), expiration ?? DateTimeOffset.UtcNow.AddDays(1));

static void Persistence()
{
    WithDatabase(path =>
    {
        var request = Request();
        ChatMessage reply;
        using (var first = new ChatStore(path))
        {
            first.AddUserMessage(request);
            reply = first.AddReply(request.RequestId, "こんにちは。ご用件をどうぞ。");
        }
        using var reopened = new ChatStore(path);
        Equal(request.Text, reopened.GetRequest(request.RequestId).Text);
        Equal(reply, reopened.ReadMessages().Messages[1]);
        Equal(0, reopened.GetStatus().QueuedCount);
        Throws<ConflictException>(() => new ChatStore(path, "other-channel").Dispose());
    });
}

static void Idempotency()
{
    using var store = new ChatStore(":memory:");
    var request = Request("  改行\nをそのまま残す  ");
    var original = store.AddUserMessage(request);
    Equal(original, store.AddUserMessage(request));
    Equal(original, store.AddUserMessage(request with { RequestId = request.RequestId.ToUpperInvariant() }));
    Throws<ConflictException>(() => store.AddUserMessage(request with { Text = "別の内容" }));
    Throws<ConflictException>(() => store.AddUserMessage(request with { Text = "" }));
    Throws<ConflictException>(() => store.AddUserMessage(request with { InReplyTo = "reply_missing" }));
    Equal(1, store.GetStatus().QueuedCount);
    var reply = store.AddReply(request.RequestId, "返事");
    Equal(reply, store.AddReply(request.RequestId, "返事"));
    Throws<ConflictException>(() => store.AddReply(request.RequestId, "別の返事"));
    Throws<ConflictException>(() => store.AddReply(request.RequestId, ""));
    Equal(2, store.ReadMessages().Messages.Count);
    Equal(original.Id, reply.InReplyTo);
}

static void Validation()
{
    using var store = new ChatStore(":memory:");
    Throws<ValidationException>(() => store.AddUserMessage(Request(" \n\t")));
    Throws<ValidationException>(() => store.AddUserMessage(Request(new string('a', 32769))));
    Throws<ValidationException>(() => store.AddUserMessage(Request() with { RequestId = "invalid" }));
    Throws<ValidationException>(() => store.AddUserMessage(Request() with { RequestId = Guid.Empty.ToString("D") }));
    Throws<ValidationException>(() => store.AddUserMessage(Request(inReplyTo: "reply_missing")));
    Equal(0, store.ReadMessages().Messages.Count);
    Equal(0, store.GetStatus().QueuedCount);
    var request = store.AddUserMessage(Request(new string('a', 32768)));
    Throws<ValidationException>(() => store.AddUserMessage(Request(inReplyTo: request.Id)));
    Throws<KeyNotFoundException>(() => store.AddReply(Guid.NewGuid().ToString("D"), "unknown"));
    Throws<ValidationException>(() => store.AddReply(request.Id, " "));
    Equal(1, store.ReadMessages().Messages.Count);
    Throws<ValidationException>(() => store.ReadMessages(-1));
    Throws<ValidationException>(() => store.ReadMessages(limit: 0));
    Throws<ValidationException>(() => store.ReadMessages(limit: 101));
    Throws<ValidationException>(() => store.GetContext(request.Id, 0));
    Throws<ValidationException>(() => store.SaveSubscription(SubscriptionFor() with { CallbackUrl = "http://example.test" }));
}

static void Pagination()
{
    using var store = new ChatStore(":memory:");
    for (var index = 0; index < 3; index++)
    {
        var request = Request($"質問{index}");
        store.AddUserMessage(request);
        store.AddReply(request.RequestId, $"回答{index}");
    }
    var all = store.ReadMessages();
    var seen = new List<ChatMessage>();
    long cursor = 0;
    MessagePage page;
    do
    {
        page = store.ReadMessages(cursor, 2);
        seen.AddRange(page.Messages);
        True(page.NextCursor > cursor, "A nonempty page advances the cursor.");
        cursor = page.NextCursor;
    } while (page.HasMore);
    True(all.Messages.SequenceEqual(seen), "Paging must preserve order and message identity.");
    var empty = store.ReadMessages(cursor, 2);
    Equal(0, empty.Messages.Count);
    Equal(cursor, empty.NextCursor);
    True(!empty.HasMore, "The exhausted cursor stays exhausted.");
    var next = store.AddUserMessage(Request());
    Equal(next, store.ReadMessages(cursor).Messages.Single());
}

static void DurableRetries()
{
    WithDatabase(path =>
    {
        var now = DateTimeOffset.UtcNow;
        var due = now.AddMinutes(10);
        var request = Request();
        PendingDelivery original;
        using (var first = new ChatStore(path))
        {
            first.SaveSubscription(SubscriptionFor());
            first.AddUserMessage(request);
            original = first.GetNextDelivery(now.AddSeconds(1))!;
            True(original is not null, "The original event is available.");
            first.MarkDeliveryFailed(original!.EventId, "https://secret.example.test/?token=secret", due);
        }
        using var reopened = new ChatStore(path);
        True(reopened.GetNextDelivery(due.AddSeconds(-1)) is null, "A retry must wait for its due time.");
        var retry = reopened.GetNextDelivery(due)!;
        Equal(original.EventId, retry.EventId);
        Equal(original.Message, retry.Message);
        Equal(1, retry.Attempts);
        True(!reopened.GetStatus().LastError!.Contains("secret", StringComparison.Ordinal), "Errors must not expose URLs or secrets.");
        reopened.MarkDelivered(retry.EventId);
        Equal(1, reopened.GetStatus().AwaitingReplyCount);
        Equal(0, reopened.GetStatus().QueuedCount);
        True(reopened.GetNextDelivery(due) is null, "Acknowledged events wait for a reply without redelivery.");
    });
}

static void QueuedBeforeSubscription()
{
    using var store = new ChatStore(":memory:");
    var first = store.AddUserMessage(Request("first"));
    var second = store.AddUserMessage(Request("second"));
    True(store.GetNextDelivery(DateTimeOffset.UtcNow) is null, "An unbound message cannot be sent.");
    Equal(2, store.GetStatus().QueuedCount);
    store.SaveSubscription(SubscriptionFor());
    var delivery = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    Equal(first.Id, delivery.Message.Id);
    store.MarkDelivered(delivery.EventId);
    Equal(second.Id, store.GetNextDelivery(DateTimeOffset.UtcNow)!.Message.Id);
}

static void ExpiredSubscription()
{
    WithDatabase(path =>
    {
        var expired = SubscriptionFor(expiration: DateTimeOffset.UtcNow.AddSeconds(-1));
        using (var first = new ChatStore(path))
        {
            first.SaveSubscription(expired);
            first.AddUserMessage(Request());
            True(!first.GetStatus().Subscribed, "An expired subscription is disconnected.");
            True(first.GetNextDelivery(DateTimeOffset.UtcNow) is null, "Expiry stops delivery.");
            first.SaveSubscription(expired with { RefreshBefore = DateTimeOffset.UtcNow.AddDays(1) });
            True(first.GetNextDelivery(DateTimeOffset.UtcNow) is not null, "A verified renewal resumes delivery.");
            first.Unsubscribe("unrelated");
            True(first.GetStatus().Subscribed, "Unsubscribing an unknown ID is idempotent.");
            first.Unsubscribe(expired.Id);
        }
        using var reopened = new ChatStore(path);
        True(reopened.GetSubscription() is null, "Unsubscription persists.");
        True(reopened.GetNextDelivery(DateTimeOffset.UtcNow) is null, "Revocation stops delivery after restart.");
        Equal(0, reopened.RetryFailedDeliveries());
    });
}

static void SubscriptionBinding()
{
    using var store = new ChatStore(":memory:");
    var subscription = SubscriptionFor();
    store.SaveSubscription(subscription);
    var request = store.AddUserMessage(Request());
    var delivery = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    store.MarkDelivered(delivery.EventId);
    Throws<ConflictException>(() => store.SaveSubscription(SubscriptionFor("other")));
    Throws<ConflictException>(() => store.SaveSubscription(subscription with { CallbackUrl = "https://other.example.test/" }));
    store.Unsubscribe(subscription.Id);
    Throws<ConflictException>(() => store.SaveSubscription(SubscriptionFor("other")));
    store.SaveSubscription(subscription with { Secret = "rotated-secret" });
    Equal("rotated-secret", store.GetSubscription()!.Secret);
    store.AddReply(request.Id, "done");
    store.SaveSubscription(SubscriptionFor("other"));
    var next = store.AddUserMessage(Request());
    var nextDelivery = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    Equal("other", nextDelivery.Subscription.Id);
    Equal(next.Id, nextDelivery.Message.Id);
}

static void ReplyBeforeReceipt()
{
    using var store = new ChatStore(":memory:");
    store.SaveSubscription(SubscriptionFor());
    var request = store.AddUserMessage(Request());
    var delivery = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    var reply = store.AddReply(request.Id, "finished before callback acknowledgement");
    store.MarkDeliveryFailed(delivery.EventId, "timeout", DateTimeOffset.UtcNow);
    True(store.GetNextDelivery(DateTimeOffset.UtcNow) is null, "A final answer stops retrying its event.");
    store.MarkDelivered(delivery.EventId);
    store.MarkDeliveryFailed(delivery.EventId, "late timeout", DateTimeOffset.UtcNow);
    Equal(0, store.GetStatus().QueuedCount);
    Equal(0, store.GetStatus().AwaitingReplyCount);
    Equal<string?>(null, store.GetStatus().LastError);
    Equal(reply, store.ReadMessages().Messages.Last());
}

static void ContextSnapshot()
{
    using var store = new ChatStore(":memory:");
    var first = store.AddUserMessage(Request("question 1"));
    var firstReply = store.AddReply(first.Id, "answer 1");
    var second = store.AddUserMessage(Request("question 2", firstReply.Id));
    store.AddReply(second.Id, "later answer");
    store.AddUserMessage(Request("future question"));
    var context = store.GetContext(second.Id);
    Equal(3, context.Count);
    Equal(second, context.Last());
    True(context.All(message => message.Sequence <= second.Sequence), "Future messages cannot enter old events.");
    Equal(firstReply, store.GetContext(second.Id, 2)[0]);
}

static void ManualRetries()
{
    using var store = new ChatStore(":memory:");
    var subscription = SubscriptionFor();
    store.SaveSubscription(subscription);
    var requests = Enumerable.Range(0, 3).Select(index => store.AddUserMessage(Request($"retry {index}"))).ToList();
    store.MarkDeliveryFailed("evt_" + requests[0].Id, "timeout", DateTimeOffset.MaxValue);
    store.MarkDeliveryFailed("evt_" + requests[1].Id, "callback_expired", DateTimeOffset.MaxValue);
    store.MarkDeliveryFailed("evt_" + requests[2].Id, "payload_too_large", DateTimeOffset.MaxValue);
    Equal(1, store.RetryFailedDeliveries());
    var transient = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    Equal(requests[0].Id, transient.Message.Id);
    Equal(0, transient.Attempts);
    store.MarkDelivered(transient.EventId);
    True(store.GetNextDelivery(DateTimeOffset.UtcNow) is null, "Manual retry preserves terminal failures.");
    store.SaveSubscription(subscription with { RefreshBefore = DateTimeOffset.UtcNow.AddDays(2) });
    var renewed = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    Equal(requests[1].Id, renewed.Message.Id);
    store.MarkDelivered(renewed.EventId);
    True(store.GetNextDelivery(DateTimeOffset.UtcNow) is null, "Oversized payloads stay suspended after renewal.");
}

static void ConcurrentRetries()
{
    using var store = new ChatStore(":memory:");
    var request = Request();
    Parallel.For(0, 12, _ => store.AddUserMessage(request));
    Parallel.For(0, 12, _ => store.AddReply(request.RequestId, "one answer"));
    Equal(2, store.ReadMessages().Messages.Count);
    Equal(0, store.GetStatus().QueuedCount);
}

static void LateFailureAfterManualRetry()
{
    WithDatabase(path =>
    {
        PendingDelivery oldAttempt;
        using (var store = new ChatStore(path))
        {
            store.SaveSubscription(SubscriptionFor());
            store.AddUserMessage(Request());
            var firstAttempt = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
            store.MarkDeliveryFailed(firstAttempt.EventId, "timeout", DateTimeOffset.UtcNow,
                firstAttempt.Generation);
            oldAttempt = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
            Equal(1, oldAttempt.Attempts);

            Equal(1, store.RetryFailedDeliveries());
            store.MarkDeliveryFailed(oldAttempt.EventId, "timeout", DateTimeOffset.MaxValue,
                oldAttempt.Generation);
            var current = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
            True(current is not null, "A late failure cannot suspend a manually reset delivery.");
            Equal(oldAttempt.Generation + 1, current!.Generation);
            Equal(0, current.Attempts);
            Equal<string?>(null, store.GetStatus().LastError);
        }

        using var reopened = new ChatStore(path);
        reopened.MarkDeliveryFailed(oldAttempt.EventId, "timeout", DateTimeOffset.MaxValue,
            oldAttempt.Generation);
        var pending = reopened.GetNextDelivery(DateTimeOffset.UtcNow)!;
        True(pending is not null, "The generation fence survives a restart.");
        Equal(oldAttempt.Generation + 1, pending!.Generation);
        Equal(0, pending.Attempts);
    });
}

static void LateFailureAfterRenewal()
{
    using var store = new ChatStore(":memory:");
    var subscription = SubscriptionFor();
    store.SaveSubscription(subscription);
    var first = store.AddUserMessage(Request("already in flight"));
    var oldAttempt = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    var second = store.AddUserMessage(Request("previously expired"));
    store.MarkDeliveryFailed("evt_" + second.Id, "callback_expired", DateTimeOffset.MaxValue, 0);

    store.SaveSubscription(subscription with
    {
        Secret = "renewed-key",
        RefreshBefore = DateTimeOffset.UtcNow.AddDays(2)
    });
    store.MarkDeliveryFailed(oldAttempt.EventId, "callback_expired", DateTimeOffset.MaxValue,
        oldAttempt.Generation);
    var current = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    True(current is not null, "A 410 response using the old key cannot suspend the renewed delivery.");
    Equal(first.Id, current!.Message.Id);
    Equal(oldAttempt.Generation + 1, current.Generation);
    Equal("renewed-key", current.Subscription.Secret);
    Equal(0, current.Attempts);
    Equal<string?>(null, store.GetStatus().LastError);

    // An acknowledgement still confirms the same immutable event even when it
    // belongs to an attempt that began before the connection was renewed.
    store.MarkDelivered(oldAttempt.EventId);
    Equal(1, store.GetStatus().AwaitingReplyCount);
    var resumed = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
    Equal(second.Id, resumed.Message.Id);
    Equal(1, resumed.Generation);
    Equal(0, resumed.Attempts);
    store.MarkDeliveryFailed(resumed.EventId, "callback_expired", DateTimeOffset.MaxValue, 0);
    Equal(resumed.EventId, store.GetNextDelivery(DateTimeOffset.UtcNow)!.EventId);
}

static void GenerationMigration()
{
    WithDatabase(path =>
    {
        var due = DateTimeOffset.UtcNow.AddMinutes(5);
        PendingDelivery original;
        using (var store = new ChatStore(path))
        {
            store.SaveSubscription(SubscriptionFor());
            store.AddUserMessage(Request("survives migration"));
            original = store.GetNextDelivery(DateTimeOffset.UtcNow)!;
            store.MarkDeliveryFailed(original.EventId, "timeout", due);
        }

        // Recreate the prior schema without the generation column while leaving
        // real saved messages, subscription and retry state in place.
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "ALTER TABLE outbox DROP COLUMN generation; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        using var upgraded = new ChatStore(path);
        True(upgraded.GetNextDelivery(due.AddSeconds(-1)) is null, "Migration preserves retry timing.");
        var pending = upgraded.GetNextDelivery(due)!;
        Equal(original.EventId, pending.EventId);
        Equal(original.Message, pending.Message);
        Equal(1, pending.Attempts);
        Equal(0, pending.Generation);
        Equal(1, upgraded.RetryFailedDeliveries());
        Equal(1, upgraded.GetNextDelivery(DateTimeOffset.UtcNow)!.Generation);
    });
}

static void WithDatabase(Action<string> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "hagoromo-regression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        test(Path.Combine(directory, "messages.db"));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
