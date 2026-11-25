using System.Text.Json;
using HagoromoChan.Contracts;
using HagoromoChan.Desktop;
using HagoromoChan.Relay;

internal static class ProactiveChecks
{
    public static void StorePersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hagoromo-proactive-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "messages.db");
            var requestId = Guid.NewGuid().ToString("D");
            var nextRequestId = Guid.NewGuid().ToString("D");
            var subscription = new Subscription("sub_proactive_test", "https://callback.example.test/events",
                "whsec_" + Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow.AddDays(1));
            ChatMessage standalone;
            RelayStatus before;
            using (var store = new ChatStore(path))
            {
                store.SaveSubscription(subscription);
                var request = store.AddUserMessage(new SendMessageRequest(requestId, "依頼済み", null));
                store.MarkDelivered("evt_" + request.Id);
                store.AddUserMessage(new SendMessageRequest(nextRequestId, "配信待ち", null));
                before = store.GetStatus();
                Equal(1, before.QueuedCount);
                Equal(1, before.AwaitingReplyCount);

                // Intentionally reuse a raw user UUID; the namespace, not luck,
                // separates correlation keys and message IDs.
                standalone = store.AddProactiveMessage(requestId, "独立したお知らせ\n本文");
                Equal("proactive_" + requestId, standalone.Id);
                Equal("assistant", standalone.Role);
                True(standalone.InReplyTo is null, "Standalone message has no request parent.");
                Equal(standalone, store.AddProactiveMessage(requestId.ToUpperInvariant(), standalone.Text));
                Throws<ConflictException>(() => store.AddProactiveMessage(requestId, "異なる本文"));
                Equal(before, store.GetStatus());
                Equal(subscription, store.GetSubscription());
                Equal(nextRequestId, store.GetNextDelivery(DateTimeOffset.UtcNow)!.Message.Id);
                Equal(3, store.ReadMessages().Messages.Count);
                True(store.GetContext(requestId).All(m => m.Id != standalone.Id),
                    "Historical request context excludes later standalone message.");
                Throws<ConflictException>(() => store.SaveSubscription(subscription with
                {
                    Id = "other_sub", CallbackUrl = "https://other.example.test/events"
                }));
            }

            using (var reopened = new ChatStore(path))
            {
                Equal(before, reopened.GetStatus());
                Equal(standalone, reopened.AddProactiveMessage(requestId, standalone.Text));
                Equal(3, reopened.ReadMessages().Messages.Count);
                var reply = reopened.AddReply(requestId, "依頼への回答");
                Equal("reply_" + requestId, reply.Id);
                Equal(requestId, reply.InReplyTo);
                Equal(0, reopened.GetStatus().AwaitingReplyCount);
                Equal(1, reopened.GetStatus().QueuedCount);
                var followup = reopened.AddUserMessage(new SendMessageRequest(Guid.NewGuid().ToString("D"),
                    "お知らせについて詳しく", standalone.Id));
                var context = reopened.GetContext(followup.Id);
                True(context.Any(m => m == standalone), "Standalone message is part of later conversation context.");
                Equal(standalone.Id, followup.InReplyTo);
                Equal(2, reopened.GetStatus().QueuedCount);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static void ValidityAndConcurrentRetry()
    {
        using var store = new ChatStore(":memory:");
        var id = Guid.NewGuid().ToString("D");
        foreach (var invalid in new[] { "", "invalid", Guid.Empty.ToString("D"), "proactive_" + id })
            Throws<ValidationException>(() => store.AddProactiveMessage(invalid, "text"));
        Throws<ValidationException>(() => store.AddProactiveMessage(id, " \n\t"));
        Throws<ValidationException>(() => store.AddProactiveMessage(id, new string('a', 32769)));
        Equal(0, store.ReadMessages().Messages.Count);

        var text = new string('あ', 32768);
        var results = new ChatMessage[16];
        Parallel.For(0, results.Length, i => results[i] = store.AddProactiveMessage(id, text));
        True(results.All(m => m == results[0]), "Concurrent retry stores one message with one timestamp and sequence.");
        Equal(1, store.ReadMessages().Messages.Count);
        True(store.GetNextDelivery(DateTimeOffset.UtcNow) is null, "Standalone does not create a user event.");
        Equal(0, store.GetStatus().QueuedCount);
        Equal(0, store.GetStatus().AwaitingReplyCount);
        Throws<KeyNotFoundException>(() => store.AddReply(id, "No corresponding request."));
    }

    public static void McpTool() => McpToolAsync().GetAwaiter().GetResult();

    private static async Task McpToolAsync()
    {
        using var store = new ChatStore(":memory:");
        using var webhooks = new WebhookClient();
        var handler = new McpHandler(store, webhooks);

        async Task<JsonElement> Call(string method, object parameters)
        {
            var request = JsonSerializer.SerializeToElement(new
            {
                jsonrpc = "2.0", id = 1, method, @params = parameters
            });
            return JsonSerializer.SerializeToElement(await handler.HandleAsync(request, CancellationToken.None));
        }

        async Task<JsonElement> Send(object arguments) =>
            await Call("tools/call", new { name = "send_message", arguments });

        static JsonElement Payload(JsonElement response)
        {
            var result = response.GetProperty("result");
            True(!result.GetProperty("isError").GetBoolean(), "Expected a successful MCP result.");
            using var parsed = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
            return parsed.RootElement.Clone();
        }

        var tools = (await Call("tools/list", new { })).GetProperty("result").GetProperty("tools")
            .EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
        Equal(3, tools.Count);
        True(tools.ContainsKey("reply") && tools.ContainsKey("get_context"), "Keep existing tools.");
        var tool = tools["send_message"];
        True(!tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Send is a write action.");
        True(tool.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean(), "Same input can be retried.");
        Equal(store.ChannelId, tool.GetProperty("inputSchema").GetProperty("properties")
            .GetProperty("channel_id").GetProperty("enum")[0].GetString());

        var id = Guid.NewGuid().ToString("D");
        var args = new { channel_id = store.ChannelId, message_id = id, text = "M04テスト" };
        var first = Payload(await Send(args));
        var second = Payload(await Send(args));
        Equal(id, first.GetProperty("message_id").GetString());
        Equal("proactive_" + id, first.GetProperty("stored_message_id").GetString());
        Equal(first.GetProperty("sequence").GetInt64(), second.GetProperty("sequence").GetInt64());
        True(first.GetProperty("stored").GetBoolean(), "Success means durably stored.");
        var conflict = await Send(new { channel_id = store.ChannelId, message_id = id, text = "別の内容" });
        True(conflict.GetProperty("result").GetProperty("isError").GetBoolean(), "Reject a conflicting retry.");
        var badId = await Send(new { channel_id = store.ChannelId, message_id = "not-a-uuid", text = "text" });
        True(badId.GetProperty("result").GetProperty("isError").GetBoolean(), "Reject invalid ID.");
        foreach (var invalid in new object[]
        {
            new { channel_id = "other", message_id = id, text = "text" },
            new { channel_id = store.ChannelId, text = "text" },
            new { channel_id = store.ChannelId, message_id = id, text = "text", request_id = id }
        })
        {
            var error = await Send(invalid);
            Equal(-32602, error.GetProperty("error").GetProperty("code").GetInt32());
        }
        var invalidReply = await Call("tools/call", new { name = "reply", arguments = new { request_id = id, text = "text" } });
        True(invalidReply.GetProperty("result").GetProperty("isError").GetBoolean(),
            "Standalone ID cannot serve as a request for reply.");
        var invalidContext = await Call("tools/call", new { name = "get_context", arguments = new { request_id = id } });
        True(invalidContext.GetProperty("result").GetProperty("isError").GetBoolean(),
            "Existing get_context remains request-scoped.");
        Equal(1, store.ReadMessages().Messages.Count);
        True(store.GetSubscription() is null, "Standalone does not create a subscription.");
        Equal(0, store.GetStatus().QueuedCount);
    }

    public static void DesktopUnread()
    {
        using var store = new ChatStore(":memory:");
        var preferences = new DesktopPreferences();
        var old = store.AddProactiveMessage(Guid.NewGuid().ToString("D"), "旧履歴");
        var page = store.ReadMessages();
        preferences.Observe(page.Messages, page.NextCursor);
        Equal(0, preferences.UnreadIds.Count);
        var request = store.AddUserMessage(new SendMessageRequest(Guid.NewGuid().ToString("D"), "質問", old.Id));
        var proactive = store.AddProactiveMessage(Guid.NewGuid().ToString("D"), "新しい自発発言");
        var reply = store.AddReply(request.Id, "回答");
        page = store.ReadMessages();
        preferences.Observe(page.Messages, page.NextCursor);
        True(preferences.UnreadIds.SetEquals(new[] { proactive.Id, reply.Id }),
            "New proactive message and reply use the same unread mechanism, not the user request.");
        preferences.UnreadIds.Remove(proactive.Id);
        preferences.Observe(page.Messages, page.NextCursor);
        True(preferences.UnreadIds.SetEquals(new[] { reply.Id }), "Re-poll does not revive a read proactive message.");
        var restored = JsonSerializer.Deserialize<DesktopPreferences>(JsonSerializer.Serialize(preferences))!;
        restored.Observe(page.Messages, page.NextCursor);
        True(restored.UnreadIds.SetEquals(new[] { reply.Id }), "Checkpoint survives restart.");
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
