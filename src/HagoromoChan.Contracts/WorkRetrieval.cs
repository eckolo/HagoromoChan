namespace HagoromoChan.Contracts;

public sealed record WorkPullRequest(string OperationId, string SourceId, string ConversationId, string Action = "latest_answer", string? MessageId = null);
public sealed record WorkPullStatus(string OperationId, string State, CloudAnswer? Answer = null, WorkSourceCandidate[]? Choices = null,
    WorkAnswerCandidate[]? AnswerChoices = null);
public sealed record WorkPullResult(string OperationId, string SourceId, string ConversationId,
    string State, string? MessageId, string? Text);

public sealed record WorkSourceCandidate(string ConversationId, string Title);

public sealed record WorkAnswerCandidate(string MessageId, string Label);
