namespace HagoromoChan.Contracts;

public sealed record CloudSource(string Id, string Kind, string Url, string Title, long LatestSequence);
public sealed record SaveCloudSourceRequest(string Kind, string Url, string Title);
public sealed record CloudAnswer(long Sequence, string SourceId, string ImportId, string Text, DateTimeOffset ReceivedAt);
public sealed record CloudAnswerPage(CloudAnswer[] Answers, long NextCursor, bool HasMore);
