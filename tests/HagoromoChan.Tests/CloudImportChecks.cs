using System.Text.Json;
using HagoromoChan.Contracts;
using HagoromoChan.Relay;
using HagoromoChan.Desktop;

internal static class CloudImportChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "hagoromo-m05-" + Guid.NewGuid().ToString("N"));
        var db = Path.Combine(root, "chat.db");
        var sendId = Guid.NewGuid().ToString("D");
        var importId = Guid.NewGuid().ToString("D");
        CloudSource first;
        CloudSource second;
        CloudAnswer original;
        try
        {
            using (var store = new ChatStore(db))
            {
                store.AddUserMessage(new SendMessageRequest(sendId, "pending", null));
                var dotBefore = store.ReadMessages();
                var dotStatus = store.GetStatus();
                var inbox = new ReactionInbox();
                var reaction = new CharacterReactionState();
                Must(inbox.ObserveCompletedRead(dotBefore.Messages, dotBefore.NextCursor).Count == 0, "Initial Dot history does not replay");
                reaction.SendAccepted(sendId, 0);
                Must(reaction.WaitingForReply && reaction.Sample(1000).Pose == ReactionPose.Thinking, "Dot request is pending");
                var firstUrl = $"https://chatgpt.com/c/{Guid.NewGuid():D}";
                var secondUrl = $"https://chatgpt.com/g/g-p-project/c/{Guid.NewGuid():D}";
                first = store.SaveCloudSource(new("chat", firstUrl + "?tracking=discard", "同名"));
                second = store.SaveCloudSource(new("work", secondUrl, "同名"));
                Must(first.Url == firstUrl && first.Id != second.Id, "Stable private source URL");
                Must(store.SaveCloudSource(new("chat", firstUrl, "更新済")).Id == first.Id, "Rename keeps identity");
                original = store.ImportCloudAnswer(first.Id, importId, "回答\n絵文字🙂");
                Parallel.For(0, 8, _ => Must(store.ImportCloudAnswer(first.Id, importId, original.Text) == original, "Retry"));
                Throws<ConflictException>(() => store.ImportCloudAnswer(first.Id, importId, "changed"));
                Throws<ConflictException>(() => store.ImportCloudAnswer(second.Id, importId, original.Text));
                Throws<KeyNotFoundException>(() => store.ImportCloudAnswer(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "unknown"));
                Throws<ValidationException>(() => store.SaveCloudSource(new("work", $"https://chatgpt.com/share/{Guid.NewGuid():D}", "share")));
                Throws<ValidationException>(() => store.SaveCloudSource(new("work", $"https://chatgpt.com.evil.test/c/{Guid.NewGuid():D}", "foreign")));
                store.ImportCloudAnswer(first.Id, Guid.NewGuid().ToString("D"), original.Text);
                store.ImportCloudAnswer(second.Id, Guid.NewGuid().ToString("D"), original.Text);
                var firstPage = store.ReadCloudAnswers(first.Id, 0, 1);
                Must(firstPage.Answers.Single() == original && firstPage.HasMore, "First source page");
                Must(store.ReadCloudAnswers(first.Id, firstPage.NextCursor).Answers.Length == 1, "Intentional re-forward");
                Must(store.ReadCloudAnswers(second.Id).Answers.Single().SourceId == second.Id, "Source isolation");
                Must(dotBefore.Messages.SequenceEqual(store.ReadMessages().Messages) &&
                    dotBefore.NextCursor == store.ReadMessages().NextCursor, "No Dot row or cursor change");
                Must(dotStatus.QueuedCount == store.GetStatus().QueuedCount, "No outbox change");
                var duringImport = store.ReadMessages();
                var importedReception = inbox.ObserveCompletedRead(duringImport.Messages, duringImport.NextCursor);
                reaction.Receive(importedReception, 1500);
                Must(importedReception.Count == 0 && reaction.WaitingForReply &&
                    reaction.Sample(1501).Pose == ReactionPose.Thinking, "Import must not animate or settle Dot");

                var reply = store.AddReply(sendId, "本物のDot返信");
                Must(store.ReadMessages(dotBefore.NextCursor).Messages.Single() == reply, "Real reply arrives exactly once");
                var completed = store.ReadMessages();
                var reception = inbox.ObserveCompletedRead(completed.Messages, completed.NextCursor);
                Must(reception.Single() == reply, "Matching Dot reply reaches the M09 observer");
                reaction.Receive(reception, 2000);
                Must(!reaction.WaitingForReply && reaction.Sample(2001).Pose == ReactionPose.Jump,
                    "Real Dot reply settles the request and animates");
                var replay = inbox.ObserveCompletedRead(completed.Messages, completed.NextCursor);
                reaction.Receive(replay, 2400);
                Must(replay.Count == 0 && reaction.Sample(2651).Pose == ReactionPose.Idle,
                    "Same completed read must not restart the reception animation");
            }
            using (var store = new ChatStore(db))
            {
                Must(store.ListCloudSources().Single(source => source.Id == first.Id).Title == "更新済", "Saved title survives restart");
                Must(store.ImportCloudAnswer(first.Id, importId, original.Text) == original, "Durable retry");
                Must(store.ReadCloudAnswers(first.Id).Answers.Length == 2 &&
                    store.ReadMessages().Messages.Count == 2, "Persisted isolated histories");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    public static void WorkWake() => WorkWakeAsync().GetAwaiter().GetResult();

    private static async Task WorkWakeAsync()
    {
        using var store = new ChatStore(":memory:");
        using var webhooks = new FixtureWorkWebhook();
        var handler = new McpHandler(store, webhooks);
        var dispatcher = new WorkWakeDispatcher(handler, webhooks);
        var dot = new Subscription("sub_fixture_dot", "https://fixture.example.test/dot",
            "fixture-dot", DateTimeOffset.UtcNow.AddHours(1));
        store.SaveSubscription(dot);
        var originalDot = store.GetSubscription();
        async Task<JsonElement> Call(string method, object parameters)
        {
            var request = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
            return JsonSerializer.SerializeToElement(await handler.HandleAsync(request, CancellationToken.None));
        }

        var eventList = (await Call("events/list", new { })).GetProperty("result").GetProperty("events");
        Must(eventList.EnumerateArray().Any(item => item.GetProperty("name").GetString() == McpHandler.EventName),
            "Dot message event is still advertised");
        var spec = eventList.EnumerateArray().Single(item => item.GetProperty("name").GetString() == McpHandler.WorkWakeEvent);
        Must(spec.GetProperty("payloadSchema").GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ToHashSet().SetEquals(new[] { "channel_id", "operation_id" }),
            "Work wake schema exposes only an operation ID and a dedicated channel");

        var pull = handler.WorkPull.Discover();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Must(webhooks.Sent.Count == 0, "No wake before separate subscription");
        var url = "https://fixture.example.test/work";
        var subscribe = await Call("events/subscribe", new
        {
            name = McpHandler.WorkWakeEvent, arguments = new { channel_id = McpHandler.WorkWakeChannel },
            delivery = new { mode = "webhook", url, secret = "whsec_" + Convert.ToBase64String(new byte[32]) }
        });
        Must(subscribe.TryGetProperty("result", out _), "Separate Work webhook accepted");
        Must(store.GetSubscription() == originalDot, "Work subscribe does not replace Dot");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Must(webhooks.Sent.SequenceEqual(new[] { (url, pull.OperationId) }), "One wake for the exact pending operation");

        async Task<JsonElement> Pending(string id) =>
            (await Call("tools/call", new { name = "get_pending_work_retrieval", arguments = new { operation_id = id } }))
                .GetProperty("result").GetProperty("structuredContent").GetProperty("pending");

        Must((await Pending(Guid.NewGuid().ToString("D"))).ValueKind == JsonValueKind.Null &&
            (await Pending(pull.OperationId)).GetProperty("operation_id").GetString() == pull.OperationId,
            "An unrelated wake ID cannot claim the head of the pending queue");
        handler.WorkPull.CompleteDiscovery(pull.OperationId, []);
        Must((await Pending(pull.OperationId)).ValueKind == JsonValueKind.Null, "Completed wakes cannot access a request");
        var unsubscribe = await Call("events/unsubscribe", new
        {
            name = McpHandler.WorkWakeEvent, arguments = new { channel_id = McpHandler.WorkWakeChannel },
            delivery = new { mode = "webhook", url }
        });
        Must(unsubscribe.TryGetProperty("result", out _) && store.GetSubscription() == originalDot,
            "Work unsubscribe also leaves Dot subscription intact");
        _ = handler.WorkPull.Discover();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Must(webhooks.Sent.Count == 1, "No wake after isolated Work unsubscribe");
    }

    private sealed class FixtureWorkWebhook : WebhookClient
    {
        public List<(string Url, string OperationId)> Sent { get; } = [];
        public override Task VerifyAsync(Subscription subscription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendWorkWakeAsync(WorkWakeRegistration registration, string operationId, CancellationToken cancellationToken)
        {
            Sent.Add((registration.Subscription.CallbackUrl, operationId));
            return Task.CompletedTask;
        }
    }

    public static void WorkWakeDurable()
    {
        var root = Path.Combine(Path.GetTempPath(), "hagoromo-work-wake-" + Guid.NewGuid().ToString("N"));
        var db = Path.Combine(root, "chat.db");
        var at = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var a = "whsec_" + Convert.ToBase64String(new byte[32]);
        var b = "whsec_" + Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        var dot = new Subscription("legacy", "https://fixture.example.test/dot", "fixture", at.AddHours(2));
        var work = new Subscription("work", "https://fixture.example.test/work", a, at.AddHours(1));
        try
        {
            using (var store = new ChatStore(db))
            {
                store.SaveSubscription(dot);
                store.SaveWorkWakeSubscription(work, at);
                store.SaveWorkWakeSubscription(work with { Secret = b }, at.AddSeconds(1));
                store.SaveWorkWakeSubscription(work with { Secret = b, RefreshBefore = at.AddHours(2) }, at.AddSeconds(2));
                Throws<ConflictException>(() => store.SaveWorkWakeSubscription(work with { Id = "other" }, at.AddSeconds(3)));
            }
            using (var store = new ChatStore(db))
            {
                var restored = store.GetWorkWakeRegistration() ?? throw new Exception("Missing Work subscription after restart");
                Must(restored.Subscription.Secret == b && restored.RotateUntil == at.AddMinutes(5).AddSeconds(1),
                    "Same-ID refresh retains replacement and original rotation window across restart");
                byte[] body = [1, 2, 3];
                var stamp = at.AddSeconds(4).ToUnixTimeSeconds();
                var expected = WebhookClient.CreateSignature(b, "fixture_event", stamp, body) + " " +
                    WebhookClient.CreateSignature(a, "fixture_event", stamp, body);
                Must(WebhookClient.WorkWakeSignatures(restored, "fixture_event", stamp, body) == expected,
                    "Work wake dual signature matches both keys during rotation");
                stamp = at.AddMinutes(6).ToUnixTimeSeconds();
                Must(WebhookClient.WorkWakeSignatures(restored, "fixture_event", stamp, body) ==
                    WebhookClient.CreateSignature(b, "fixture_event", stamp, body),
                    "Old key is no longer used after rotation");
                store.RemoveWorkWakeSubscription("unrelated");
                Must(store.GetWorkWakeRegistration() is not null, "Unrelated unsubscribe cannot clear Work");
                store.RemoveWorkWakeSubscription("work");
                Must(store.GetWorkWakeRegistration() is null, "Exact Work unsubscribe persists");
                Must(store.GetSubscription()?.Id == dot.Id, "Work lifecycle leaves durable Dot subscription unchanged");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    public static void DefaultMcp() => DefaultMcpAsync().GetAwaiter().GetResult();

    private static async Task DefaultMcpAsync()
    {
        using var store = new ChatStore(":memory:");
        using var webhooks = new WebhookClient();
        var handler = new McpHandler(store, webhooks);
        var fixtures = M05LocalPreview.Seed(store);
        var waiting = store.AddUserMessage(new SendMessageRequest(Guid.NewGuid().ToString("D"), "pending", null));

        async Task<JsonElement> Call(string method, object parameters)
        {
            var request = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
            return JsonSerializer.SerializeToElement(await handler.HandleAsync(request, CancellationToken.None));
        }

        var names = (await Call("tools/list", new { })).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToHashSet();
        Must(names.SetEquals(new[] { "get_context", "reply", "send_message", "import_cloud_answer", "get_pending_work_retrieval", "complete_work_retrieval", "complete_work_discovery", "complete_work_answer_choices" }),
            "Normal MCP advertises the new tool without dropping a legacy tool");

        async Task<JsonElement> Import(string source, string id, string body) =>
            (await Call("tools/call", new
            {
                name = "import_cloud_answer", arguments = new { source_id = source, import_id = id, text = body }
            })).GetProperty("result");

        var chat = fixtures.Single(item => item.Source.Kind == "chat").Source;
        var work = fixtures.Single(item => item.Source.Kind == "work").Source;
        Throws<ValidationException>(() => handler.WorkPull.Start(chat.Id));
        async Task<JsonElement> WorkTool(string name, object arguments) =>
            (await Call("tools/call", new { name, arguments })).GetProperty("result");

        const string retrieve = "get_pending_work_retrieval";
        const string complete = "complete_work_retrieval";
        Must((await WorkTool(retrieve, new { })).GetProperty("structuredContent").GetProperty("pending")
            .ValueKind == JsonValueKind.Null, "No Work read before an explicit SSP request");
        var discovery = handler.WorkPull.Discover();
        Must(handler.WorkPull.Next() is { Action: "discover" }, "Only explicit SSP discovery is queued");
        Must((await WorkTool("complete_work_discovery", new
        {
            operation_id = discovery.OperationId, choices = new[] { new { conversation_id = "work/A", title = "Invalid" } }
        })).GetProperty("isError").GetBoolean(), "Path separators cannot enter provider IDs");
        var malformedDiscovery = await Call("tools/call", new
        {
            name = "complete_work_discovery", arguments = new { operation_id = discovery.OperationId, choices = new[] { 7 } }
        });
        Must(malformedDiscovery.GetProperty("error").GetProperty("code").GetInt32() == -32602,
            "A non-object Work discovery choice is rejected as invalid params");
        const string found = "work_Fixture_A";
        const string sibling = "work_Fixture_a";
        Must(!(await WorkTool("complete_work_discovery", new
        {
            operation_id = discovery.OperationId, choices = new[] { new { conversation_id = found, title = "Fixture Work" },
                new { conversation_id = sibling, title = "Fixture Work" } }
        })).GetProperty("isError").GetBoolean(), "Verified Work choice accepted");
        Must(store.ListCloudSources().Length == fixtures.Length, "Discovery does not register before selection");
        Throws<KeyNotFoundException>(() => handler.WorkPull.SelectChoice(discovery.OperationId, Guid.NewGuid().ToString("D")));
        var selected = handler.WorkPull.SelectChoice(discovery.OperationId, found);
        Must(selected.Kind == "work" && selected.Title == "Fixture Work" && selected.Url == "" &&
            handler.WorkPull.SelectChoice(discovery.OperationId, sibling).Id != selected.Id &&
            handler.WorkPull.SelectChoice(discovery.OperationId, found).Id == selected.Id,
            "Selection alone registers the authorized candidate idempotently");
        var pull = handler.WorkPull.Start(selected.Id);
        Must(handler.WorkPull.Start(selected.Id) == pull, "Coalesce another request while pending");
        var queued = (await WorkTool(retrieve, new { })).GetProperty("structuredContent").GetProperty("pending");
        const string conversation = found;
        Must(queued.GetProperty("conversation_id").GetString() == conversation &&
            queued.GetProperty("operation_id").GetString() == pull.OperationId, "Reader gets exact registered Work identity");
        var message = "msg_Fixture_A";
        const string exact = "  complete Work body\nsecond line  ";
        object Completion(string operation, string source, string conversationId, string text) => new
        {
            operation_id = operation, source_id = source, conversation_id = conversationId,
            state = "available", message_id = message, text
        };
        Must((await WorkTool(complete, Completion(pull.OperationId, chat.Id, conversation, exact)))
            .GetProperty("isError").GetBoolean(), "Wrong registered source rejected");
        Must((await WorkTool(complete, Completion(pull.OperationId, selected.Id, Guid.NewGuid().ToString("D"), exact)))
            .GetProperty("isError").GetBoolean(), "Wrong conversation rejected");
        Must(!(await WorkTool(complete, Completion(pull.OperationId, selected.Id, conversation, exact)))
            .GetProperty("isError").GetBoolean(), "Exact completed answer accepted");
        Must(!(await WorkTool(complete, Completion(pull.OperationId, selected.Id, conversation, exact)))
            .GetProperty("isError").GetBoolean(), "Exact result replay accepted");
        Must((await WorkTool(complete, Completion(pull.OperationId, selected.Id, conversation, exact + "!")))
            .GetProperty("isError").GetBoolean(), "Changed result rejected");
        Must(handler.WorkPull.Status(pull.OperationId).Answer?.Text == exact, "SSP receives exact original body");
        var pullAgain = handler.WorkPull.Start(selected.Id);
        Must(!(await WorkTool(complete, Completion(pullAgain.OperationId, selected.Id, conversation, exact)))
            .GetProperty("isError").GetBoolean() &&
            store.ReadCloudAnswers(selected.Id).Answers.Count(a => a.Text == exact) == 1,
            "Fetching the same original Work message twice leaves one persisted row");
        var caseDistinct = handler.WorkPull.Start(selected.Id);
        message = "msg_Fixture_a";
        Must(!(await WorkTool(complete, Completion(caseDistinct.OperationId, selected.Id, conversation, exact)))
            .GetProperty("isError").GetBoolean() &&
            store.ReadCloudAnswers(selected.Id).Answers.Count(a => a.Text == exact) == 2,
            "Same body is not an identifier: two opaque message IDs that differ only in case remain distinct");
        var denied = handler.WorkPull.Start(selected.Id);
        Must(!(await WorkTool(complete, new
        {
            operation_id = denied.OperationId, source_id = selected.Id, conversation_id = conversation, state = "not_authorized"
        })).GetProperty("isError").GetBoolean() &&
            handler.WorkPull.Status(denied.OperationId) is { State: "not_authorized", Answer: null },
            "Denied reads never create a body");
        var answerList = handler.WorkPull.ListAnswers(selected.Id);
        var listRequest = handler.WorkPull.Next();
        Must(listRequest is { Action: "list_answers" } && listRequest.ConversationId == conversation,
            "SSP asks only for answers in the selected Work");
        var malformedAnswerChoices = await Call("tools/call", new
        {
            name = "complete_work_answer_choices", arguments = new
            {
                operation_id = answerList.OperationId, source_id = selected.Id, conversation_id = conversation,
                choices = new string?[] { null }
            }
        });
        Must(malformedAnswerChoices.GetProperty("error").GetProperty("code").GetInt32() == -32602,
            "A null Work answer choice is rejected as invalid params");
        const string olderMessage = "msg_older_completed";
        const string latestMessage = "msg_latest_completed";
        Must(!(await WorkTool("complete_work_answer_choices", new
        {
            operation_id = answerList.OperationId, source_id = selected.Id, conversation_id = conversation,
            choices = new[] { new { message_id = olderMessage, label = "Same label" },
                new { message_id = latestMessage, label = "Same label" } }
        })).GetProperty("isError").GetBoolean(), "Completed choices belong to exact Work");
        Must(handler.WorkPull.Status(answerList.OperationId).AnswerChoices?.Length == 2,
            "Both provider IDs are selectable without using label identity");
        Throws<KeyNotFoundException>(() => handler.WorkPull.SelectAnswer(answerList.OperationId, "msg_unlisted"));
        var selectedAnswer = handler.WorkPull.SelectAnswer(answerList.OperationId, olderMessage);
        Must(handler.WorkPull.Next() is { Action: "selected_answer", MessageId: olderMessage },
            "Selection binds old answer even if another answer is newer");
        message = latestMessage;
        Must((await WorkTool(complete, Completion(selectedAnswer.OperationId, selected.Id, conversation, "wrong body")))
            .GetProperty("isError").GetBoolean(), "Returning a different latest answer is rejected");
        message = olderMessage;
        Must(!(await WorkTool(complete, Completion(selectedAnswer.OperationId, selected.Id, conversation, "older full body")))
            .GetProperty("isError").GetBoolean() &&
            handler.WorkPull.Status(selectedAnswer.OperationId).Answer?.Text == "older full body",
            "Exact chosen answer is persisted and displayed");

        foreach (var source in new[] { chat, work })
        {
            var id = Guid.NewGuid().ToString("D");
            var body = $"selected answer from {source.Kind}";
            Must(!(await Import(source.Id, id, body)).GetProperty("isError").GetBoolean(), "Explicit import accepted");
            Must(!(await Import(source.Id, id, body)).GetProperty("isError").GetBoolean(), "Identical retry accepted");
            Must((await Import(source.Id, id, body + "!")).GetProperty("isError").GetBoolean(), "Conflicting retry rejected");
            Must(store.ReadCloudAnswers(source.Id).Answers.Count(a => a.Text == body) == 1,
                "Exactly one imported row in its registered source");
        }
        Must((await Import(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "unregistered"))
            .GetProperty("isError").GetBoolean(), "Unknown registrations rejected");
        Must(store.ReadMessages().Messages.Single() == waiting && store.GetStatus().QueuedCount == 1,
            "Tool uses a separate history without settling or enqueueing a Dot message");
    }

    public static void PreviewSeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "hagoromo-m05-fixture-" + Guid.NewGuid().ToString("N"));
        var db = Path.Combine(root, "conversations.db");
        try
        {
            (CloudSource Source, CloudAnswer Answer)[] samples;
            using (var store = new ChatStore(db))
                samples = M05LocalPreview.Seed(store);
            using (var store = new ChatStore(db))
            {
                Must(samples.Length == 2 && store.ListCloudSources().Length == 2, "Two preview sources");
                Must(samples.Select(sample => sample.Source.Kind).ToHashSet().SetEquals(new[] { "chat", "work" }),
                    "Both preview kinds");
                foreach (var sample in samples)
                {
                    Must(store.ListCloudSources().Single(source => source.Id == sample.Source.Id).Title == sample.Source.Title,
                        "Preview title survived restart");
                    Must(store.ReadCloudAnswers(sample.Source.Id).Answers.Single() == sample.Answer,
                        "Fixture answer stayed in its own source");
                }
                Must(store.ReadMessages().Messages.Count == 0 && store.GetStatus().QueuedCount == 0,
                    "Preview data does not populate Dot or the outbox");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Must(bool value, string what)
    {
        if (!value) throw new InvalidOperationException(what);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
