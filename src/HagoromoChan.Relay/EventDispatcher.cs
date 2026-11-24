namespace HagoromoChan.Relay;

public sealed class EventDispatcher(ChatStore store, WebhookClient webhooks, ILogger<EventDispatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pending = store.GetNextDelivery(DateTimeOffset.UtcNow);
            if (pending is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await webhooks.SendAsync(pending, stoppingToken);
                store.MarkDelivered(pending.EventId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (WebhookDeliveryException exception)
            {
                // Accepted events are never re-sent because a final answer takes a long time.
                var canRetry = exception.Retryable && pending.Attempts < 7;
                var nextAttempt = canRetry
                    ? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, pending.Attempts + 1)))
                    : DateTimeOffset.MaxValue;
                store.MarkDeliveryFailed(pending.EventId, exception.Reason, nextAttempt, pending.Generation);
                logger.LogWarning("MCP event delivery failed ({Reason}). Request {RequestId} remains queued.",
                    exception.Reason, pending.Message.Id);
            }
        }
    }
}
