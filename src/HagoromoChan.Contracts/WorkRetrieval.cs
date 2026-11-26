namespace HagoromoChan.Contracts;

public sealed record WorkPullRequest(string OperationId, string SourceId, string ConversationId, string Action = "latest_answer");
public sealed record WorkPullStatus(string OperationId, string State, CloudAnswer? Answer = null, WorkSourceCandidate[]? Choices = null);
public sealed record WorkPullResult(string OperationId, string SourceId, string ConversationId,
    string State, string? MessageId, string? Text);

public sealed record WorkSourceCandidate(string ConversationId, string Title);
