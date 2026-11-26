using HagoromoChan.Contracts;
using HagoromoChan.Relay;

internal static class CloudImportChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "hagoromo-m05-" + Guid.NewGuid().ToString("N"));
        var db = Path.Combine(root, "chat.db");
        var sendId = Guid.NewGuid().ToString("D");
        var importId = Guid.NewGuid().ToString("D");
        CloudSource first;
        CloudSource second;
        CloudAnswer original;
        try
        {
            using (var store = new ChatStore(db))
            {
                store.AddUserMessage(new SendMessageRequest(sendId, "pending", null));
                var dotBefore = store.ReadMessages();
                var dotStatus = store.GetStatus();
                var firstUrl = $"https://chatgpt.com/c/{Guid.NewGuid():D}";
                var secondUrl = $"https://chatgpt.com/g/g-p-project/c/{Guid.NewGuid():D}";
                first = store.SaveCloudSource(new("chat", firstUrl + "?tracking=discard", "同名"));
                second = store.SaveCloudSource(new("work", secondUrl, "同名"));
                Must(first.Url == firstUrl && first.Id != second.Id, "Stable private source URL");
                Must(store.SaveCloudSource(new("chat", firstUrl, "更新済")).Id == first.Id, "Rename keeps identity");
                original = store.ImportCloudAnswer(first.Id, importId, "回答\n絵文字🙂");
                Parallel.For(0, 8, _ => Must(store.ImportCloudAnswer(first.Id, importId, original.Text) == original, "Retry"));
                Throws<ConflictException>(() => store.ImportCloudAnswer(first.Id, importId, "changed"));
                Throws<ConflictException>(() => store.ImportCloudAnswer(second.Id, importId, original.Text));
                Throws<KeyNotFoundException>(() => store.ImportCloudAnswer(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "unknown"));
                Throws<ValidationException>(() => store.SaveCloudSource(new("work", $"https://chatgpt.com/share/{Guid.NewGuid():D}", "share")));
                Throws<ValidationException>(() => store.SaveCloudSource(new("work", $"https://chatgpt.com.evil.test/c/{Guid.NewGuid():D}", "foreign")));
                store.ImportCloudAnswer(first.Id, Guid.NewGuid().ToString("D"), original.Text);
                store.ImportCloudAnswer(second.Id, Guid.NewGuid().ToString("D"), original.Text);
                var firstPage = store.ReadCloudAnswers(first.Id, 0, 1);
                Must(firstPage.Answers.Single() == original && firstPage.HasMore, "First source page");
                Must(store.ReadCloudAnswers(first.Id, firstPage.NextCursor).Answers.Length == 1, "Intentional re-forward");
                Must(store.ReadCloudAnswers(second.Id).Answers.Single().SourceId == second.Id, "Source isolation");
                Must(dotBefore.Messages.SequenceEqual(store.ReadMessages().Messages) &&
                    dotBefore.NextCursor == store.ReadMessages().NextCursor, "No Dot row or cursor change");
                Must(dotStatus.QueuedCount == store.GetStatus().QueuedCount, "No outbox change");
                var reply = store.AddReply(sendId, "本物のDot返信");
                Must(store.ReadMessages(dotBefore.NextCursor).Messages.Single() == reply, "Real reply arrives exactly once");
            }
            using (var store = new ChatStore(db))
            {
                Must(store.ListCloudSources().Single(source => source.Id == first.Id).Title == "更新済", "Saved title survives restart");
                Must(store.ImportCloudAnswer(first.Id, importId, original.Text) == original, "Durable retry");
                Must(store.ReadCloudAnswers(first.Id).Answers.Length == 2 &&
                    store.ReadMessages().Messages.Count == 2, "Persisted isolated histories");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Must(bool value, string what)
    {
        if (!value) throw new InvalidOperationException(what);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
