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

    public WorkPullStatus Start(string sourceId) => Start(sourceId, "latest_answer", null);

    public WorkPullStatus ListAnswers(string sourceId) => Start(sourceId, "list_answers", null);

    private WorkPullStatus Start(string sourceId, string action, string? messageId)
    {
        lock (gate)
        {
            Expire();
            var conversation = store.WorkConversationId(sourceId);
            var existing = pending.Values.FirstOrDefault(p => p.Request.SourceId == sourceId &&
                p.Request.Action == action && p.Request.MessageId == messageId && p.Status.State == "pending");
            if (existing is not null) return existing.Status;
            if (pending.Count >= 32)
                throw new ValidationException("Too many recent retrieval requests. Try again later.");
            var id = Guid.NewGuid().ToString("D");
            var status = new WorkPullStatus(id, "pending");
            pending.Add(id, new(new(id, sourceId, conversation, action, messageId),
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
                !IsProviderId(c.ConversationId) || string.IsNullOrWhiteSpace(c.Title) ||
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
            return store.SaveDiscoveredWork(picked);
        }
    }

    public WorkPullStatus CompleteAnswerChoices(string operationId, string sourceId, string conversationId,
        WorkAnswerCandidate[] choices)
    {
        lock (gate)
        {
            Expire();
            if (!pending.TryGetValue(operationId, out var item) || item.Request.Action != "list_answers")
                throw new KeyNotFoundException();
            if (item.Request.SourceId != sourceId || item.Request.ConversationId != conversationId)
                throw new ValidationException("Answer list identity does not match.");
            if (choices.Length > 25 || choices.Any(c => c is null || !IsProviderId(c.MessageId) ||
                string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > 200 || c.Label.Any(char.IsControl)) ||
                choices.Select(c => c.MessageId).Distinct(StringComparer.Ordinal).Count() != choices.Length)
                throw new ValidationException("Invalid Work answer choices.");
            if (item.Result is not null) throw new ConflictException("Answer list already failed.");
            if (item.Status.AnswerChoices is { } prior)
                return prior.SequenceEqual(choices) ? item.Status : throw new ConflictException("Answer list changed.");
            var status = new WorkPullStatus(operationId, "answer_choices", AnswerChoices: choices.ToArray());
            pending[operationId] = item with { Status = status };
            return status;
        }
    }

    public WorkPullStatus SelectAnswer(string operationId, string messageId)
    {
        lock (gate)
        {
            Expire();
            if (!pending.TryGetValue(operationId, out var item) || item.Status.AnswerChoices is not { } choices ||
                !choices.Any(c => c.MessageId == messageId))
                throw new KeyNotFoundException();
            return Start(item.Request.SourceId, "selected_answer", messageId);
        }
    }

    public WorkPullRequest? Next(string? operationId = null) =>
        PendingRequests().FirstOrDefault(p => operationId is null || p.OperationId == operationId);

    public WorkPullRequest[] PendingRequests()
    {
        lock (gate)
        {
            Expire();
            return pending.Values.Where(p => p.Status.State == "pending").Select(p => p.Request).ToArray();
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
            if (!pending.TryGetValue(result.OperationId, out var item) || item.Request.Action is not ("latest_answer" or "selected_answer" or "list_answers") ||
                item.Status.AnswerChoices is not null)
                throw new KeyNotFoundException();
            if (item.Request.SourceId != result.SourceId || item.Request.ConversationId != result.ConversationId)
                throw new ValidationException("Retrieval identity does not match.");
            if (item.Result is not null)
                return item.Result == result ? item.Status : throw new ConflictException("Retrieval result changed.");
            CloudAnswer? answer = null;
            if (result.State == "available")
            {
                if (item.Request.Action == "list_answers" || !IsProviderId(result.MessageId) || string.IsNullOrWhiteSpace(result.Text) ||
                    (item.Request.MessageId is { } selected && selected != result.MessageId))
                    throw new ValidationException("A message ID and complete body are required.");
                // Stable per registered source and original answer, independent of the request/retry UUID.
                // Provider IDs are case-sensitive, opaque and restricted to unreserved URI characters.
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"m05-work:{result.SourceId}:{result.MessageId}"));
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

    private static bool IsProviderId(string? value) =>
        value is { Length: >= 1 and <= 200 } && value is not ("." or "..") &&
        !string.Equals(value, Guid.Empty.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~');

    private void Expire()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in pending.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            pending.Remove(id);
    }
}
