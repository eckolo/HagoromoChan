using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HagoromoChan.Contracts;

namespace HagoromoChan.Relay;

public sealed class McpHandler(ChatStore store, WebhookClient webhooks)
{
    public const string EventName = "message.created";
    public const string SupportedChannel = "hagoromo-mini";
    private const string LatestProtocol = "2025-11-25";
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim subscriptionGate = new(1, 1);

    public async Task<object?> HandleAsync(JsonElement request, CancellationToken cancellationToken)
    {
        if (request.ValueKind != JsonValueKind.Object)
            return Error(null, -32600, "A JSON-RPC request object is required.");

        // JSON-RPC notifications have no response, including malformed notifications.
        if (!request.TryGetProperty("id", out var id))
            return null;
        if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
            return Error(null, -32600, "Invalid JSON-RPC request identifier.");
        if (!request.TryGetProperty("jsonrpc", out var version) ||
            version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
            !request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
        {
            return Error(id.Clone(), -32600, "Invalid JSON-RPC request.");
        }

        try
        {
            var parameters = request.TryGetProperty("params", out var supplied) ? supplied : default;
            if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
                throw InvalidParams("Parameters must be an object.");

            object result = method.GetString() switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new { },
                "tools/list" => ListTools(parameters),
                "tools/call" => CallTool(parameters),
                "events/list" => ListEvents(parameters),
                "events/subscribe" => await SubscribeAsync(parameters, cancellationToken),
                "events/unsubscribe" => await UnsubscribeAsync(parameters, cancellationToken),
                _ => throw new RpcException(-32601, "Method not found.")
            };
            return new { jsonrpc = "2.0", id = id.Clone(), result };
        }
        catch (RpcException exception)
        {
            return Error(id.Clone(), exception.Code, exception.Message, exception.Reason);
        }
        catch (ValidationException)
        {
            return Error(id.Clone(), -32602, "The supplied parameters are invalid.");
        }
        catch (ConflictException)
        {
            return Error(id.Clone(), -32000, "The subscription conflicts with pending requests.");
        }
        catch (KeyNotFoundException)
        {
            return Error(id.Clone(), -32602, "The requested record was not found.");
        }
    }

    private static object Initialize(JsonElement parameters)
    {
        var requested = RequiredString(parameters, "protocolVersion");
        var protocol = requested is "2025-03-26" or "2025-06-18" or LatestProtocol
            ? requested : LatestProtocol;
        return new
        {
            protocolVersion = protocol,
            capabilities = new { tools = new { }, events = new { } },
            serverInfo = new { name = "hagoromo-chan", version = "0.1.0" },
            instructions = "This plugin receives requests from the owner's HagoromoChan desktop app. " +
                "Use get_context to read a request and its preceding app conversation. " +
                "Use reply with the matching request_id to deliver the final answer to the app. " +
                "The app receives only text explicitly submitted through reply."
        };
    }

    private static object ListTools(JsonElement parameters)
    {
        ValidateCursor(parameters);
        return new
        {
            tools = new object[]
            {
                new
                {
                    name = "get_context",
                    description = "Read a desktop request and up to twelve messages ending at that request. " +
                        "Use this to recover full text when event context is abbreviated and to resolve follow-up questions.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { request_id = new { type = "string", description = "The request_id from message.created." } },
                        required = new[] { "request_id" },
                        additionalProperties = false
                    },
                    annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
                },
                new
                {
                    name = "reply",
                    description = "Deliver the final plain-text answer to the owner's desktop request. " +
                        "Use the same request_id. Repeating the same answer is safe. A different second final answer is rejected.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            request_id = new { type = "string", description = "The request_id being answered." },
                            text = new { type = "string", minLength = 1, maxLength = 32768, description = "The complete final answer for the desktop app." }
                        },
                        required = new[] { "request_id", "text" },
                        additionalProperties = false
                    },
                    annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false }
                }
            }
        };
    }

    private object CallTool(JsonElement parameters)
    {
        var name = RequiredString(parameters, "name");
        if (name is not ("get_context" or "reply"))
            throw InvalidParams("Unknown tool.");
        var arguments = RequiredObject(parameters, "arguments");
        ValidateProperties(arguments, name == "reply" ? ["request_id", "text"] : ["request_id"]);
        var requestId = RequiredString(arguments, "request_id");
        var text = name == "reply" ? RequiredString(arguments, "text") : null;
        try
        {
            if (name == "get_context")
            {
                return ToolResult(new
                {
                    request_id = requestId,
                    channel_id = store.ChannelId,
                    messages = store.GetContext(requestId, 12).Select(DescribeMessage).ToArray()
                });
            }

            var reply = store.AddReply(requestId, text!);
            return ToolResult(new { request_id = requestId, reply_id = reply.Id, sequence = reply.Sequence });
        }
        catch (KeyNotFoundException)
        {
            return ToolError("request_not_found");
        }
        catch (ValidationException)
        {
            return ToolError("invalid_request_or_reply");
        }
        catch (ConflictException)
        {
            return ToolError("a_different_final_reply_already_exists");
        }
    }

    private object ListEvents(JsonElement parameters)
    {
        ValidateCursor(parameters);
        return new
        {
            events = new[]
            {
                new
                {
                    name = EventName,
                    description = "A message submitted by the owner in the HagoromoChan desktop app.",
                    delivery = new[] { "webhook" },
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            channel_id = new { type = "string", @enum = new[] { store.ChannelId }, description = "The owner's desktop conversation channel." }
                        },
                        required = new[] { "channel_id" },
                        additionalProperties = false
                    },
                    payloadSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            request_id = new { type = "string" },
                            channel_id = new { type = "string", @enum = new[] { store.ChannelId } },
                            text = new { type = "string" },
                            in_reply_to = new { type = new[] { "string", "null" } },
                            context = new
                            {
                                type = "array",
                                description = "Recent desktop context. Use get_context for full preceding messages.",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        message_id = new { type = "string" },
                                        role = new { type = "string", @enum = new[] { "user", "assistant" } },
                                        text = new { type = "string" },
                                        in_reply_to = new { type = new[] { "string", "null" } },
                                        created_at = new { type = "string", format = "date-time" },
                                        text_truncated = new { type = "boolean" }
                                    },
                                    required = new[] { "message_id", "role", "text", "in_reply_to", "created_at", "text_truncated" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "request_id", "channel_id", "text", "in_reply_to", "context" },
                        additionalProperties = false
                    }
                }
            }
        };
    }

    private async Task<object> SubscribeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var callback = ValidateEventParameters(parameters, requireSecret: true);
        ValidateCursor(parameters);
        var lifetime = SubscriptionLifetime;
        if (parameters.TryGetProperty("ttlMs", out var ttl) && ttl.ValueKind != JsonValueKind.Null)
        {
            if (ttl.ValueKind != JsonValueKind.Number || !ttl.TryGetInt64(out var milliseconds) || milliseconds <= 0)
                throw InvalidParams("ttlMs must be a positive integer or null.");
            lifetime = TimeSpan.FromMilliseconds(Math.Min(milliseconds, (long)SubscriptionLifetime.TotalMilliseconds));
        }

        var subscription = new Subscription(SubscriptionId(callback.Url), callback.Url, callback.Secret!, DateTimeOffset.UtcNow.Add(lifetime));
        try
        {
            WebhookClient.ValidateSubscription(subscription);
        }
        catch (WebhookDeliveryException)
        {
            throw InvalidParams("The callback URL or signing secret is invalid.");
        }

        await subscriptionGate.WaitAsync(cancellationToken);
        try
        {
            var previous = store.GetSubscription();
            try
            {
                await webhooks.VerifyAsync(subscription, cancellationToken);
            }
            catch (WebhookDeliveryException exception)
            {
                throw new RpcException(-32015, "Callback verification failed.", exception.Reason);
            }
            // Start the granted lifetime after verification, which can involve a network request.
            subscription = subscription with { RefreshBefore = DateTimeOffset.UtcNow.Add(lifetime) };
            store.SaveSubscription(subscription);
            webhooks.RememberSecretRotation(previous, subscription);
            return new { id = subscription.Id, refreshBefore = subscription.RefreshBefore, cursor = (string?)null, truncated = false };
        }
        finally
        {
            subscriptionGate.Release();
        }
    }

    private async Task<object> UnsubscribeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var callback = ValidateEventParameters(parameters, requireSecret: false);
        await subscriptionGate.WaitAsync(cancellationToken);
        try
        {
            store.Unsubscribe(SubscriptionId(callback.Url));
            return new { };
        }
        finally
        {
            subscriptionGate.Release();
        }
    }

    private (string Url, string? Secret) ValidateEventParameters(JsonElement parameters, bool requireSecret)
    {
        if (RequiredString(parameters, "name") != EventName)
            throw InvalidParams("Unknown event.");
        var arguments = RequiredObject(parameters, "arguments");
        ValidateProperties(arguments, ["channel_id"]);
        if (RequiredString(arguments, "channel_id") != store.ChannelId)
            throw InvalidParams("Unknown channel.");
        var delivery = RequiredObject(parameters, "delivery");
        if (RequiredString(delivery, "mode") != "webhook")
            throw InvalidParams("Only webhook delivery is supported.");
        var url = RequiredString(delivery, "url");
        var secret = requireSecret ? RequiredString(delivery, "secret") : null;
        try
        {
            url = WebhookClient.ValidateCallbackUri(url).AbsoluteUri;
        }
        catch (WebhookDeliveryException)
        {
            throw InvalidParams("The callback URL is invalid.");
        }
        return (url, secret);
    }

    private string SubscriptionId(string callbackUrl)
    {
        // Each relay has one authenticated owner. Channel and event complete its subscription identity.
        var identity = callbackUrl + "\n" + EventName + "\n" + store.ChannelId;
        return "sub_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    internal static object DescribeMessage(ChatMessage message) => new
    {
        message_id = message.Id,
        role = message.Role,
        text = message.Text,
        in_reply_to = message.InReplyTo,
        created_at = message.CreatedAt,
        text_truncated = false
    };

    private static object ToolResult(object result) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(result) } },
        isError = false
    };

    private static object ToolError(string error) => new
    {
        content = new[] { new { type = "text", text = error } },
        isError = true
    };

    private static object Error(object? id, int code, string message, string? reason = null)
    {
        object error = reason is null ? new { code, message } : new { code, message, data = new { reason } };
        return new { jsonrpc = "2.0", id, error };
    }

    private static JsonElement RequiredObject(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidParams($"{property} must be an object.");
        }
        return value;
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw InvalidParams($"{property} must be a nonempty string.");
        }
        return value.GetString()!;
    }

    private static void ValidateProperties(JsonElement arguments, string[] accepted)
    {
        if (arguments.EnumerateObject().Any(property => !accepted.Contains(property.Name, StringComparer.Ordinal)))
            throw InvalidParams("An unrecognized argument was supplied.");
    }

    private static void ValidateCursor(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("cursor", out var cursor) &&
            cursor.ValueKind != JsonValueKind.Null)
        {
            throw InvalidParams("This endpoint has no additional pages or replay cursor.");
        }
    }

    private static RpcException InvalidParams(string message) => new(-32602, message);

    private sealed class RpcException(int code, string message, string? reason = null) : Exception(message)
    {
        public int Code { get; } = code;
        public string? Reason { get; } = reason;
    }
}
