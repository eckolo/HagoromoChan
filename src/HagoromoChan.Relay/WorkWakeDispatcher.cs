using HagoromoChan.Contracts;

namespace HagoromoChan.Relay;

// The wake is only a hint. Subscription state, retry counts and events are
// separate from the Dot message subscription and contain no transcript data.
public sealed class WorkWakeDispatcher(McpHandler handler, WebhookClient webhooks) : BackgroundService
{
    private readonly Dictionary<string, (int Attempts, DateTimeOffset After)> deliveries = new(StringComparer.Ordinal);

    public async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = handler.WorkPull.PendingRequests();
        var ids = pending.Select(item => item.OperationId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in deliveries.Keys.Where(id => !ids.Contains(id)).ToArray()) deliveries.Remove(stale);

        var subscription = handler.WorkWakeSubscription;
        if (subscription is null || subscription.RefreshBefore <= now) return;
        var next = pending.FirstOrDefault(item => !deliveries.TryGetValue(item.OperationId, out var receipt) || receipt.After <= now);
        if (next is null) return;
        deliveries.TryGetValue(next.OperationId, out var previous);
        var attempt = previous.Attempts + 1;
        try
        {
            await webhooks.SendWorkWakeAsync(subscription, next.OperationId, cancellationToken);
            deliveries[next.OperationId] = (attempt, DateTimeOffset.MaxValue);
        }
        catch (WebhookDeliveryException error)
        {
            deliveries[next.OperationId] = (attempt, error.Retryable && attempt < 3
                ? now.AddSeconds(1 << attempt) : DateTimeOffset.MaxValue);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchOnceAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
