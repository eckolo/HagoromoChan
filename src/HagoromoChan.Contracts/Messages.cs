namespace HagoromoChan.Contracts;

public sealed record ChatMessage(
    long Sequence,
    string Id,
    string Role,
    string Text,
    string? InReplyTo,
    DateTimeOffset CreatedAt);

public sealed record SendMessageRequest(string RequestId, string Text, string? InReplyTo);

public sealed record MessagePage(IReadOnlyList<ChatMessage> Messages, long NextCursor, bool HasMore);

public sealed record RelayStatus(
    string ChannelId,
    bool Subscribed,
    DateTimeOffset? SubscriptionExpiresAt,
    int QueuedCount,
    int AwaitingReplyCount,
    string? LastError);

public sealed record ApiError(string Error);
