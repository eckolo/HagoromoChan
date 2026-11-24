using HagoromoChan.Contracts;

namespace HagoromoChan.Relay;

public sealed record Subscription(string Id, string CallbackUrl, string Secret, DateTimeOffset RefreshBefore);

public sealed record PendingDelivery(
    string EventId,
    ChatMessage Message,
    Subscription Subscription,
    int Attempts,
    IReadOnlyList<ChatMessage> Context,
    int Generation = 0);

public sealed class ConflictException(string message) : Exception(message);

public sealed class ValidationException(string message) : Exception(message);
