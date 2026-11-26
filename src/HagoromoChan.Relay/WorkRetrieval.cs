using System.Security.Cryptography;
using System.Text;
using HagoromoChan.Contracts;

namespace HagoromoChan.Relay;

// This mailbox is a protocol seam, not a ChatGPT reader. Only an authenticated
// authorized worker may resolve an explicit Work request; no web session is used.
public sealed class WorkRetrieval(ChatStore store)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private sealed record Pending(WorkPullRequest Request, DateTimeOffset Expires,
        WorkPullStatus Status, WorkPullResult? Result = null);

    public WorkPullStatus Start(string sourceId)
    {
        lock (gate)
        {
            Expire();
            var source = store.ListCloudSources().SingleOrDefault(s => s.Id == sourceId)
                ?? throw new KeyNotFoundException();
            if (source.Kind != "work")
                throw new ValidationException("Only registered Work conversations can be retrieved.");
            var existing = pending.Values.FirstOrDefault(p => p.Request.SourceId == sourceId && p.Status.State == "pending");
            if (existing is not null) return existing.Status;
            if (pending.Count >= 32)
                throw new ValidationException("Too many recent retrieval requests. Try again later.");
            var id = Guid.NewGuid().ToString("D");
            var conversation = new Uri(source.Url).AbsolutePath.TrimEnd('/').Split('/')[^1];
            var status = new WorkPullStatus(id, "pending");
            pending.Add(id, new(new(id, source.Id, Guid.ParseExact(conversation, "D").ToString("D")),
                DateTimeOffset.UtcNow + Lifetime, status));
            return status;
        }
    }

    public WorkPullStatus Discover()
    {
        lock (gate)
        {
            Expire();
            var existing = pending.Values.FirstOrDefault(p => p.Request.Action == "discover" && p.Status.State == "pending");
            if (existing is not null) return existing.Status;
            if (pending.Count >= 32) throw new ValidationException("Too many recent retrieval requests.");
            var id = Guid.NewGuid().ToString("D");
            var status = new WorkPullStatus(id, "pending");
            pending.Add(id, new(new(id, "", "", "discover"), DateTimeOffset.UtcNow + Lifetime, status));
            return status;
        }
    }

    public WorkPullStatus CompleteDiscovery(string operationId, WorkSourceCandidate[] choices)
    {
        lock (gate)
        {
            Expire();
            if (!pending.TryGetValue(operationId, out var item) || item.Request.Action != "discover")
                throw new KeyNotFoundException();
            if (choices.Length > 25 || choices.Any(c => c is null ||
                !Guid.TryParseExact(c.ConversationId, "D", out var id) || id == Guid.Empty ||
                c.ConversationId != id.ToString("D") || string.IsNullOrWhiteSpace(c.Title) ||
                c.Title.Length > 200 || c.Title.Any(char.IsControl)) ||
                choices.Select(c => c.ConversationId).Distinct(StringComparer.Ordinal).Count() != choices.Length)
                throw new ValidationException("Invalid authorized Work choices.");
            if (item.Status.Choices is { } prior)
                return prior.SequenceEqual(choices) ? item.Status : throw new ConflictException("Discovery result changed.");
            var status = new WorkPullStatus(operationId, "choices", Choices: choices.ToArray());
            pending[operationId] = item with { Status = status };
            return status;
        }
    }

    public CloudSource SelectChoice(string operationId, string conversationId)
    {
        lock (gate)
        {
            Expire();
            if (!pending.TryGetValue(operationId, out var item) || item.Status.Choices is not { } choices)
                throw new KeyNotFoundException();
            var picked = choices.SingleOrDefault(c => c.ConversationId == conversationId)
                ?? throw new KeyNotFoundException();
            var existing = store.ListCloudSources().FirstOrDefault(source => source.Kind == "work" &&
                new Uri(source.Url).AbsolutePath.TrimEnd('/').Split('/')[^1].Equals(picked.ConversationId, StringComparison.OrdinalIgnoreCase));
            return existing ?? store.SaveCloudSource(new("work", $"https://chatgpt.com/c/{picked.ConversationId}", picked.Title));
        }
    }

    public WorkPullRequest? Next()
    {
        lock (gate)
        {
            Expire();
            return pending.Values.FirstOrDefault(p => p.Status.State == "pending")?.Request;
        }
    }

    public WorkPullStatus Status(string operationId)
    {
        lock (gate)
        {
            Expire();
            return pending.TryGetValue(operationId, out var item) ? item.Status : new(operationId, "expired");
        }
    }

    public WorkPullStatus Complete(WorkPullResult result)
    {
        lock (gate)
        {
            Expire();
            if (!pending.TryGetValue(result.OperationId, out var item) || item.Request.Action != "latest_answer")
                throw new KeyNotFoundException();
            if (item.Request.SourceId != result.SourceId || item.Request.ConversationId != result.ConversationId)
                throw new ValidationException("Retrieval identity does not match.");
            if (item.Result is not null)
                return item.Result == result ? item.Status : throw new ConflictException("Retrieval result changed.");
            CloudAnswer? answer = null;
            if (result.State == "available")
            {
                if (!Guid.TryParseExact(result.MessageId, "D", out var message) || message == Guid.Empty ||
                    string.IsNullOrWhiteSpace(result.Text))
                    throw new ValidationException("A message ID and complete body are required.");
                // Stable per registered source and original answer, independent of the request/retry UUID.
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"m05-work:{result.SourceId}:{message:D}"));
                var importId = new Guid(hash.AsSpan(0, 16)).ToString("D");
                answer = store.ImportCloudAnswer(result.SourceId, importId, result.Text);
            }
            else if (result.State is not ("unavailable" or "not_authorized" or "not_found") ||
                result.Text is not null || result.MessageId is not null)
                throw new ValidationException("Invalid retrieval outcome.");
            var status = new WorkPullStatus(result.OperationId, result.State, answer);
            pending[result.OperationId] = item with { Status = status, Result = result };
            return status;
        }
    }

    private void Expire()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in pending.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            pending.Remove(id);
    }
}
