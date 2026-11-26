using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HagoromoChan.Contracts;

namespace HagoromoChan.Relay;

public sealed class McpHandler(ChatStore store, WebhookClient webhooks)
{
    public WorkRetrieval WorkPull { get; } = new(store);

    public const string EventName = "message.created";
    public const string SupportedChannel = "hagoromo-mini";
    public const string WorkWakeEvent = "work.retrieval.requested";
    public const string WorkWakeChannel = "hagoromo-work-retrieval";
    internal WorkWakeRegistration? WorkWakeSubscription => store.GetWorkWakeRegistration();
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
                "For a standalone message with no desktop request, use send_message only within the owner's authorized task and plugin permissions. " +
                "Never use it instead of reply for a request, and retry with the same message_id and exact text. " +
                "For an explicitly selected answer from a registered cloud Chat or Work, use import_cloud_answer. " +
                "Do not import automatically or treat an imported answer as a Dot reply."
        };
    }

    private object ListTools(JsonElement parameters)
    {
        ValidateCursor(parameters);
        var tools = new List<object>
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
                },
                new
                {
                    name = "send_message",
                    description = "Append a standalone message to the owner's HagoromoChan desktop conversation, " +
                        "not a reply to a desktop request. Use for an authorized proactive update or explicit request to notify the desktop. " +
                        "Generate one nonzero UUID in standard hyphenated form for each intended message and reuse the same ID and exact text on retry. " +
                        "Success means stored by the relay, not read by the owner. Do not duplicate a final reply with this tool.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            channel_id = new { type = "string", @enum = new[] { store.ChannelId }, description = "The owner's desktop channel." },
                            message_id = new { type = "string", description = "A caller-chosen stable UUID in D format for this standalone message; reuse on retries." },
                            text = new { type = "string", minLength = 1, maxLength = 32768, description = "The complete standalone plain-text message for the desktop." }
                        },
                        required = new[] { "channel_id", "message_id", "text" },
                        additionalProperties = false
                    },
                    annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false }
                }
        };
        tools.Add(new
        {
            name = "import_cloud_answer",
            description = "Store one answer explicitly selected by the owner in its originating cloud Chat or Work. " +
                "Use the source_id issued by this app when the same conversation was registered. " +
                "Use a new UUID for each intentional forwarding, and reuse its ID, source and exact text on retry. " +
                "Do not use as spontaneous forwarding or as a Dot reply. This does not read or send to ChatGPT.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    source_id = new { type = "string", description = "Registration ID supplied by the owner for the originating conversation." },
                    import_id = new { type = "string", description = "Unique UUID in D format for this forwarding." },
                    text = new { type = "string", minLength = 1, maxLength = 32768, description = "The selected answer, verbatim." }
                },
                required = new[] { "source_id", "import_id", "text" },
                additionalProperties = false
            },
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false }
        });
        tools.Add(new
        {
            name = "get_pending_work_retrieval",
            description = "Read an explicit SSP Work request. Triggered workers must pass the exact wake operation_id. Only authorized Work readers may call.",
            inputSchema = new
            {
                type = "object",
                properties = new { operation_id = new { type = "string" } },
                additionalProperties = false
            },
            annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
        });
        tools.Add(new
        {
            name = "complete_work_retrieval",
            description = "Resolve an explicit Windows SSP request. For available, supply the requested completed assistant answer's original ID and exact complete body (the selected ID, when supplied) from the authorized matching Work transcript. Do not summarize, infer, copy a user prompt, or post to that Work.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    operation_id = new { type = "string" }, source_id = new { type = "string" },
                    conversation_id = new { type = "string" },
                    state = new { type = "string", @enum = new[] { "available", "unavailable", "not_authorized", "not_found" } },
                    message_id = new { type = "string" }, text = new { type = "string", maxLength = 32768 }
                },
                required = new[] { "operation_id", "source_id", "conversation_id", "state" }, additionalProperties = false
            },
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false }
        });
        tools.Add(new
        {
            name = "complete_work_discovery",
            description = "Return up to 25 authorized cloud Work choices for an explicit SSP discovery request. Include only verified case-sensitive opaque Work conversation IDs and independently verified titles, never ordinary Chat; return an empty list when no authorized Work is available.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    operation_id = new { type = "string" },
                    choices = new
                    {
                        type = "array", maxItems = 25,
                        items = new
                        {
                            type = "object",
                            properties = new { conversation_id = new { type = "string" }, title = new { type = "string", maxLength = 200 } },
                            required = new[] { "conversation_id", "title" }, additionalProperties = false
                        }
                    }
                },
                required = new[] { "operation_id", "choices" }, additionalProperties = false
            },
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false }
        });
        tools.Add(new
        {
            name = "complete_work_answer_choices",
            description = "Return up to 25 verified completed assistant answers for the exact requested Work conversation. Labels are display-only; preserve the original case-sensitive message IDs. Do not provide incomplete or ambiguous items.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    operation_id = new { type = "string" }, source_id = new { type = "string" },
                    conversation_id = new { type = "string" },
                    choices = new
                    {
                        type = "array", maxItems = 25,
                        items = new
                        {
                            type = "object",
                            properties = new { message_id = new { type = "string" }, label = new { type = "string", maxLength = 200 } },
                            required = new[] { "message_id", "label" }, additionalProperties = false
                        }
                    }
                },
                required = new[] { "operation_id", "source_id", "conversation_id", "choices" }, additionalProperties = false
            },
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false }
        });
        return new { tools };
    }

    private object CallTool(JsonElement parameters)
    {
        var name = RequiredString(parameters, "name");
        if (name is "get_pending_work_retrieval" or "complete_work_retrieval" or "complete_work_discovery" or "complete_work_answer_choices")
            return HandleWorkTool(name, RequiredObject(parameters, "arguments"));
        if (name == "import_cloud_answer")
            return ImportCloudAnswer(RequiredObject(parameters, "arguments"));
        if (name is not ("get_context" or "reply" or "send_message"))
            throw InvalidParams("Unknown tool.");
        var arguments = RequiredObject(parameters, "arguments");
        if (name == "send_message")
            return SendStandaloneMessage(arguments);
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

    private object HandleWorkTool(string name, JsonElement arguments)
    {
        if (name == "get_pending_work_retrieval")
        {
            ValidateProperties(arguments, ["operation_id"]);
            var exact = arguments.TryGetProperty("operation_id", out _) ? RequiredString(arguments, "operation_id") : null;
            var item = WorkPull.Next(exact);
            return WorkToolResult(new { pending = item is null ? null : new
            {
                operation_id = item.OperationId, action = item.Action, source_id = item.SourceId, conversation_id = item.ConversationId, message_id = item.MessageId
            } });
        }

        if (name == "complete_work_answer_choices")
        {
            ValidateProperties(arguments, ["operation_id", "source_id", "conversation_id", "choices"]);
            if (!arguments.TryGetProperty("choices", out var supplied) || supplied.ValueKind != JsonValueKind.Array ||
                supplied.GetArrayLength() > 25)
                throw InvalidParams("Expected bounded Work answer choices.");
            var choices = supplied.EnumerateArray().Select(entry =>
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    throw InvalidParams("Each Work choice must be an object.");
                ValidateProperties(entry, ["message_id", "label"]);
                return new WorkAnswerCandidate(RequiredString(entry, "message_id"), RequiredString(entry, "label"));
            }).ToArray();
            try
            {
                var receipt = WorkPull.CompleteAnswerChoices(RequiredString(arguments, "operation_id"),
                    RequiredString(arguments, "source_id"), RequiredString(arguments, "conversation_id"), choices);
                return WorkToolResult(new { count = receipt.AnswerChoices!.Length });
            }
            catch (ValidationException) { return ToolError("invalid_work_answer_choices"); }
            catch (ConflictException) { return ToolError("different_work_answer_choices_already_exists"); }
            catch (KeyNotFoundException) { return ToolError("work_answer_choices_request_not_found"); }
        }

        if (name == "complete_work_discovery")
        {
            ValidateProperties(arguments, ["operation_id", "choices"]);
            if (!arguments.TryGetProperty("choices", out var supplied) || supplied.ValueKind != JsonValueKind.Array ||
                supplied.GetArrayLength() > 25)
                throw InvalidParams("Expected bounded Work choices.");
            var choices = supplied.EnumerateArray().Select(entry =>
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    throw InvalidParams("Each Work choice must be an object.");
                ValidateProperties(entry, ["conversation_id", "title"]);
                return new WorkSourceCandidate(RequiredString(entry, "conversation_id"), RequiredString(entry, "title"));
            }).ToArray();
            try
            {
                var receipt = WorkPull.CompleteDiscovery(RequiredString(arguments, "operation_id"), choices);
                return WorkToolResult(new { count = receipt.Choices!.Length });
            }
            catch (ValidationException) { return ToolError("invalid_work_discovery_result"); }
            catch (ConflictException) { return ToolError("different_work_discovery_result_already_exists"); }
            catch (KeyNotFoundException) { return ToolError("work_discovery_request_not_found"); }
        }

        ValidateProperties(arguments, ["operation_id", "source_id", "conversation_id", "state", "message_id", "text"]);
        string? Optional(string key) => arguments.TryGetProperty(key, out _) ? RequiredString(arguments, key) : null;
        try
        {
            var receipt = WorkPull.Complete(new(RequiredString(arguments, "operation_id"),
                RequiredString(arguments, "source_id"), RequiredString(arguments, "conversation_id"),
                RequiredString(arguments, "state"), Optional("message_id"), Optional("text")));
            return WorkToolResult(new { state = receipt.State, stored = receipt.Answer is not null });
        }
        catch (ValidationException) { return ToolError("invalid_work_retrieval_result"); }
        catch (ConflictException) { return ToolError("different_work_retrieval_result_already_exists"); }
        catch (KeyNotFoundException) { return ToolError("work_retrieval_request_not_found"); }
    }

    private static object WorkToolResult(object data) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(data) } },
        structuredContent = data,
        isError = false
    };

    private object ImportCloudAnswer(JsonElement arguments)
    {
        ValidateProperties(arguments, ["source_id", "import_id", "text"]);
        try
        {
            var answer = store.ImportCloudAnswer(RequiredString(arguments, "source_id"),
                RequiredString(arguments, "import_id"), RequiredString(arguments, "text"));
            return ToolResult(new { source_id = answer.SourceId, import_id = answer.ImportId, stored = true });
        }
        catch (ValidationException) { return ToolError("invalid_cloud_answer"); }
        catch (ConflictException) { return ToolError("different_import_already_exists"); }
        catch (KeyNotFoundException) { return ToolError("source_not_registered"); }
    }

    private object SendStandaloneMessage(JsonElement arguments)
    {
        ValidateProperties(arguments, ["channel_id", "message_id", "text"]);
        if (RequiredString(arguments, "channel_id") != store.ChannelId)
            throw InvalidParams("Unknown channel.");
        var messageId = RequiredString(arguments, "message_id");
        var text = RequiredString(arguments, "text");
        try
        {
            var message = store.AddProactiveMessage(messageId, text);
            return ToolResult(new { channel_id = store.ChannelId,
                message_id = Guid.Parse(messageId).ToString("D"), stored_message_id = message.Id,
                sequence = message.Sequence, stored = true });
        }
        catch (ValidationException)
        {
            return ToolError("invalid_message_id_or_text");
        }
        catch (ConflictException)
        {
            return ToolError("a_different_message_already_exists");
        }
    }

    private object ListEvents(JsonElement parameters)
    {
        ValidateCursor(parameters);
        return new
        {
            events = new object[]
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
                },
                new
                {
                    name = WorkWakeEvent,
                    description = "An explicit SSP Work retrieval operation is pending. The payload is a wake hint only.",
                    delivery = new[] { "webhook" },
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { channel_id = new { type = "string", @enum = new[] { WorkWakeChannel } } },
                        required = new[] { "channel_id" }, additionalProperties = false
                    },
                    payloadSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            channel_id = new { type = "string", @enum = new[] { WorkWakeChannel } },
                            operation_id = new { type = "string" }
                        },
                        required = new[] { "channel_id", "operation_id" }, additionalProperties = false
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

        var subscription = new Subscription(SubscriptionId(callback.Url, callback.Event), callback.Url, callback.Secret!, DateTimeOffset.UtcNow.Add(lifetime));
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
            var isWork = callback.Event == WorkWakeEvent;
            var previous = isWork ? WorkWakeSubscription?.Subscription : store.GetSubscription();
            if (isWork && previous is not null && previous.RefreshBefore > DateTimeOffset.UtcNow && previous.Id != subscription.Id)
                throw new ConflictException("The Work wake subscription has an active owner.");
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
            if (isWork) store.SaveWorkWakeSubscription(subscription, DateTimeOffset.UtcNow);
            else
            {
                store.SaveSubscription(subscription);
                webhooks.RememberSecretRotation(previous, subscription);
            }
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
            var id = SubscriptionId(callback.Url, callback.Event);
            if (callback.Event == WorkWakeEvent) store.RemoveWorkWakeSubscription(id);
            else store.Unsubscribe(id);
            return new { };
        }
        finally
        {
            subscriptionGate.Release();
        }
    }

    private (string Event, string Url, string? Secret) ValidateEventParameters(JsonElement parameters, bool requireSecret)
    {
        var eventName = RequiredString(parameters, "name");
        if (eventName is not (EventName or WorkWakeEvent))
            throw InvalidParams("Unknown event.");
        var arguments = RequiredObject(parameters, "arguments");
        ValidateProperties(arguments, ["channel_id"]);
        if (RequiredString(arguments, "channel_id") != (eventName == WorkWakeEvent ? WorkWakeChannel : store.ChannelId))
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
        return (eventName, url, secret);
    }

    private string SubscriptionId(string callbackUrl, string eventName)
    {
        // Each relay has one authenticated owner. Channel and event complete its subscription identity.
        var identity = callbackUrl + "\n" + eventName + "\n" +
            (eventName == WorkWakeEvent ? WorkWakeChannel : store.ChannelId);
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
