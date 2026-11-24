using System.Net;
using System.Text;
using System.Text.Json;
using HagoromoChan.Contracts;
using HagoromoChan.Relay;

public static class ProtocolChecks
{
    private const string TestSecret = "whsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string CallbackUrl = "https://callback.example.test/events/hagoromo";

    public static void RunAll()
    {
        SignatureMatchesIndependentVector();
        PrivateAddressesAndInvalidUrlsAreRejected();
        ProductionVerifierRejectsLoopbackAsync().GetAwaiter().GetResult();
        FailedVerificationLeavesStoreUnboundAsync().GetAwaiter().GetResult();
        TwoConversationRoundsAndUnsubscribeAsync().GetAwaiter().GetResult();
        InvalidProtocolRequestsHaveExpectedErrorsAsync().GetAwaiter().GetResult();
    }

    private static void SignatureMatchesIndependentVector()
    {
        // Expected bytes were computed independently with Python's hashlib/hmac implementation.
        var body = Encoding.UTF8.GetBytes("""{"eventId":"evt_0123456789abcdef","data":{"text":"こんにちは\nline"}}""");
        var signature = WebhookClient.CreateSignature(TestSecret, "evt_0123456789abcdef", 1764000000, body);
        Equal("v1,9a2odGijYALgJkYUVZS15MaYixpn3z/PVqrL2MMerJo=", signature);
        True(signature != WebhookClient.CreateSignature(TestSecret, "evt_0123456789abcdef", 1764000001, body),
            "Changing the signing timestamp must change the signature.");
        True(signature != WebhookClient.CreateSignature(TestSecret, "different-event", 1764000000, body),
            "The event identifier is covered by the signature.");
        Throws<WebhookDeliveryException>(() => WebhookClient.CreateSignature("whsec_invalid", "id", 1, body));
        Throws<WebhookDeliveryException>(() => WebhookClient.CreateSignature(
            "whsec_" + Convert.ToBase64String(new byte[8]), "id", 1, body));
    }

    private static void PrivateAddressesAndInvalidUrlsAreRejected()
    {
        string[] rejected =
        [
            "0.0.0.0", "10.1.2.3", "100.100.100.200", "127.0.0.1", "169.254.169.254",
            "172.16.0.1", "172.31.255.255", "192.168.1.1", "192.0.0.1", "192.0.2.1",
            "198.18.0.1", "198.51.100.1", "203.0.113.1", "224.0.0.1", "255.255.255.255",
            "168.63.129.16", "::", "::1", "::ffff:127.0.0.1", "::ffff:10.0.0.1",
            "fe80::1", "fc00::1", "ff02::1", "2001::1", "2001:db8::1", "2002:7f00:1::1", "3fff::1"
        ];
        foreach (var address in rejected)
            True(!WebhookClient.IsPublicAddress(IPAddress.Parse(address)), $"Non-public address {address} must be rejected.");

        string[] accepted = ["8.8.8.8", "1.1.1.1", "::ffff:8.8.8.8", "2001:4860:4860::8888", "2606:4700:4700::1111"];
        foreach (var address in accepted)
            True(WebhookClient.IsPublicAddress(IPAddress.Parse(address)), $"Public address {address} must be accepted.");

        string[] invalidUrls =
        [
            "http://example.test/callback", "ftp://example.test/callback", "/relative",
            "https://user:password@example.test/callback", "https://example.test/callback#fragment"
        ];
        foreach (var url in invalidUrls)
            Throws<WebhookDeliveryException>(() => WebhookClient.ValidateCallbackUri(url));
        Equal(CallbackUrl, WebhookClient.ValidateCallbackUri(CallbackUrl).AbsoluteUri);
    }

    private static async Task ProductionVerifierRejectsLoopbackAsync()
    {
        using var webhooks = new WebhookClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var subscription = new Subscription("sub_loopback", "https://127.0.0.1:9/callback",
            TestSecret, DateTimeOffset.UtcNow.AddHours(1));
        var exception = await ThrowsAsync<WebhookDeliveryException>(() => webhooks.VerifyAsync(subscription, timeout.Token));
        Equal("non_public_callback", exception.Reason);
        True(!exception.Retryable, "A private callback address is permanently invalid.");
    }

    private static async Task FailedVerificationLeavesStoreUnboundAsync()
    {
        using var store = new ChatStore(":memory:");
        using var webhooks = new TestWebhookClient { FailVerification = true };
        var handler = new McpHandler(store, webhooks);
        store.AddUserMessage(NewMessage("This message must wait for a verified subscription."));

        var response = await CallAsync(handler, "events/subscribe", SubscriptionArguments());
        Equal(-32015, response.GetProperty("error").GetProperty("code").GetInt32());
        Equal("challenge_failed", response.GetProperty("error").GetProperty("data").GetProperty("reason").GetString());
        True(store.GetSubscription() is null, "Failed verification must not bind the queued message.");
        True(store.GetNextDelivery(DateTimeOffset.UtcNow) is null, "Unverified callbacks must never receive an event.");

        webhooks.FailVerification = false;
        _ = Result(await CallAsync(handler, "events/subscribe", SubscriptionArguments()));
        True(store.GetNextDelivery(DateTimeOffset.UtcNow) is not null, "Successful verification makes the original queued message available.");
        Equal(2, webhooks.VerificationCount);
    }

    private static async Task TwoConversationRoundsAndUnsubscribeAsync()
    {
        using var store = new ChatStore(":memory:");
        using var webhooks = new TestWebhookClient();
        var handler = new McpHandler(store, webhooks);
        var initialize = Result(await CallAsync(handler, "initialize", new { protocolVersion = "2025-03-26" }));
        Equal("2025-03-26", initialize.GetProperty("protocolVersion").GetString());
        True(initialize.GetProperty("capabilities").TryGetProperty("events", out _), "Initialization advertises event support.");
        var negotiated = Result(await CallAsync(handler, "initialize", new { protocolVersion = "unknown-version" }));
        Equal("2025-11-25", negotiated.GetProperty("protocolVersion").GetString());

        var toolList = Result(await CallAsync(handler, "tools/list"));
        var tools = toolList.GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        True(tools["get_context"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Context is a read-only tool.");
        True(!tools["reply"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Reply declares its write effect.");
        True(tools["reply"].GetProperty("annotations").GetProperty("idempotentHint").GetBoolean(), "Retrying an identical reply is safe.");
        True(!tools["reply"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean(), "Reply appends a message.");

        var events = Result(await CallAsync(handler, "events/list")).GetProperty("events");
        Equal(McpHandler.EventName, events[0].GetProperty("name").GetString());
        Equal(store.ChannelId, events[0].GetProperty("inputSchema").GetProperty("properties").GetProperty("channel_id").GetProperty("enum")[0].GetString());

        var subscriptionResult = Result(await CallAsync(handler, "events/subscribe", SubscriptionArguments()));
        var firstSubscription = store.GetSubscription()!;
        Equal(firstSubscription.Id, subscriptionResult.GetProperty("id").GetString());
        True(firstSubscription.RefreshBefore > DateTimeOffset.UtcNow.AddDays(6), "The default subscription lifetime lasts seven days.");
        var renewed = Result(await CallAsync(handler, "events/subscribe", SubscriptionArguments()));
        Equal(firstSubscription.Id, renewed.GetProperty("id").GetString());

        var first = store.AddUserMessage(NewMessage("明日の予定を一つ教えて。"));
        await DeliverNextAsync(store, webhooks);
        var replyArguments = new { request_id = first.Id, text = "午前中に資料を確認します。" };
        var firstReply = ToolPayload(await CallAsync(handler, "tools/call", new { name = "reply", arguments = replyArguments }));
        var repeatedReply = ToolPayload(await CallAsync(handler, "tools/call", new { name = "reply", arguments = replyArguments }));
        Equal(firstReply.GetProperty("reply_id").GetString(), repeatedReply.GetProperty("reply_id").GetString());
        Equal(2, store.ReadMessages().Messages.Count);

        var conflictingReply = Result(await CallAsync(handler, "tools/call", new
        {
            name = "reply", arguments = new { request_id = first.Id, text = "違う最終回答" }
        }));
        True(conflictingReply.GetProperty("isError").GetBoolean(), "A conflicting reply must remain a tool error.");
        Equal(2, store.ReadMessages().Messages.Count);

        var second = store.AddUserMessage(NewMessage("その確認を午後に変更して。", firstReply.GetProperty("reply_id").GetString()));
        var context = ToolPayload(await CallAsync(handler, "tools/call", new
        {
            name = "get_context", arguments = new { request_id = second.Id }
        })).GetProperty("messages");
        Equal(3, context.GetArrayLength());
        Equal("午前中に資料を確認します。", context[1].GetProperty("text").GetString());
        Equal(second.Id, context[2].GetProperty("message_id").GetString());
        Equal(firstReply.GetProperty("reply_id").GetString(), context[2].GetProperty("in_reply_to").GetString());
        await DeliverNextAsync(store, webhooks);
        _ = ToolPayload(await CallAsync(handler, "tools/call", new
        {
            name = "reply", arguments = new { request_id = second.Id, text = "午後に資料を確認します。" }
        }));
        Equal(4, store.ReadMessages().Messages.Count);
        Equal(2, webhooks.Deliveries.Count);
        Equal("evt_" + first.Id, webhooks.Deliveries[0].EventId);
        Equal("evt_" + second.Id, webhooks.Deliveries[1].EventId);

        store.AddUserMessage(NewMessage("解除後には配信しないメッセージ"));
        _ = Result(await CallAsync(handler, "events/unsubscribe", UnsubscribeArguments(CallbackUrl + "/unknown")));
        True(store.GetSubscription() is not null, "Unsubscribing another callback does not remove the current subscription.");
        _ = Result(await CallAsync(handler, "events/unsubscribe", UnsubscribeArguments(CallbackUrl)));
        _ = Result(await CallAsync(handler, "events/unsubscribe", UnsubscribeArguments(CallbackUrl)));
        True(store.GetSubscription() is null, "Unsubscribe is idempotent.");
        True(store.GetNextDelivery(DateTimeOffset.UtcNow) is null, "Unsubscribe stops queued delivery.");
    }

    private static async Task InvalidProtocolRequestsHaveExpectedErrorsAsync()
    {
        using var store = new ChatStore(":memory:");
        using var webhooks = new TestWebhookClient();
        var handler = new McpHandler(store, webhooks);
        string[] invalidRequests =
        [
            "[]", "null", "42",
            """{"jsonrpc":"2.0","id":{},"method":"ping"}""",
            """{"jsonrpc":"1.0","id":1,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":1,"method":42}"""
        ];
        foreach (var request in invalidRequests)
        {
            using var parsed = JsonDocument.Parse(request);
            var response = JsonSerializer.SerializeToElement(await handler.HandleAsync(parsed.RootElement, CancellationToken.None));
            Equal(-32600, response.GetProperty("error").GetProperty("code").GetInt32());
        }

        var notification = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0", method = "events/subscribe", @params = SubscriptionArguments()
        });
        True(await handler.HandleAsync(notification, CancellationToken.None) is null, "Notifications receive no JSON-RPC response.");
        Equal(0, webhooks.VerificationCount);

        var badParameters = await CallAsync(handler, "ping", new[] { 1 });
        Equal(-32602, badParameters.GetProperty("error").GetProperty("code").GetInt32());
        var unknownMethod = await CallAsync(handler, "missing/method");
        Equal(-32601, unknownMethod.GetProperty("error").GetProperty("code").GetInt32());

        object[] badToolArguments =
        [
            new { name = "unknown", arguments = new { request_id = Guid.NewGuid().ToString("D") } },
            new { name = "reply", arguments = new { request_id = 42, text = "reply" } },
            new { name = "reply", arguments = new { request_id = Guid.NewGuid().ToString("D") } },
            new { name = "get_context", arguments = new { request_id = Guid.NewGuid().ToString("D"), unexpected = true } }
        ];
        foreach (var arguments in badToolArguments)
        {
            var response = await CallAsync(handler, "tools/call", arguments);
            Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
        }
        var unknownRequest = Result(await CallAsync(handler, "tools/call", new
        {
            name = "get_context", arguments = new { request_id = Guid.NewGuid().ToString("D") }
        }));
        True(unknownRequest.GetProperty("isError").GetBoolean(), "An unknown request produces a tool error.");

        var wrongChannel = await CallAsync(handler, "events/subscribe", SubscriptionArguments("different-channel"));
        Equal(-32602, wrongChannel.GetProperty("error").GetProperty("code").GetInt32());
        var invalidSecret = await CallAsync(handler, "events/subscribe", SubscriptionArguments(secret: "whsec_invalid"));
        Equal(-32602, invalidSecret.GetProperty("error").GetProperty("code").GetInt32());
        Equal(0, webhooks.VerificationCount);
        True(store.GetSubscription() is null, "Invalid subscriptions must not change stored state.");
    }

    private static async Task DeliverNextAsync(ChatStore store, TestWebhookClient webhooks)
    {
        var delivery = store.GetNextDelivery(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("Expected a queued delivery.");
        await webhooks.SendAsync(delivery, CancellationToken.None);
        store.MarkDelivered(delivery.EventId);
    }

    private static SendMessageRequest NewMessage(string text, string? inReplyTo = null) =>
        new(Guid.NewGuid().ToString("D"), text, inReplyTo);

    private static object SubscriptionArguments(string channel = McpHandler.SupportedChannel, string secret = TestSecret) => new
    {
        name = McpHandler.EventName,
        arguments = new { channel_id = channel },
        delivery = new { mode = "webhook", url = CallbackUrl, secret }
    };

    private static object UnsubscribeArguments(string url) => new
    {
        name = McpHandler.EventName,
        arguments = new { channel_id = McpHandler.SupportedChannel },
        delivery = new { mode = "webhook", url }
    };

    private static async Task<JsonElement> CallAsync(McpHandler handler, string method, object? parameters = null)
    {
        var request = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { }
        });
        var response = await handler.HandleAsync(request, CancellationToken.None);
        return JsonSerializer.SerializeToElement(response);
    }

    private static JsonElement Result(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException("Unexpected JSON-RPC error " + error.GetProperty("code").GetInt32());
        return response.GetProperty("result");
    }

    private static JsonElement ToolPayload(JsonElement response)
    {
        var result = Result(response);
        True(!result.GetProperty("isError").GetBoolean(), "Expected a successful tool result.");
        using var parsed = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        return parsed.RootElement.Clone();
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
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

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class TestWebhookClient : WebhookClient
    {
        public bool FailVerification { get; set; }
        public int VerificationCount { get; private set; }
        public List<PendingDelivery> Deliveries { get; } = [];

        public override Task VerifyAsync(Subscription subscription, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerificationCount++;
            if (FailVerification)
                throw new WebhookDeliveryException("challenge_failed", false);
            return Task.CompletedTask;
        }

        public override Task SendAsync(PendingDelivery delivery, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deliveries.Add(delivery);
            return Task.CompletedTask;
        }
    }
}
