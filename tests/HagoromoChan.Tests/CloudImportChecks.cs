using System.Text.Json;
using HagoromoChan.Contracts;
using HagoromoChan.Relay;
using HagoromoChan.Desktop;

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
                var inbox = new ReactionInbox();
                var reaction = new CharacterReactionState();
                Must(inbox.ObserveCompletedRead(dotBefore.Messages, dotBefore.NextCursor).Count == 0, "Initial Dot history does not replay");
                reaction.SendAccepted(sendId, 0);
                Must(reaction.WaitingForReply && reaction.Sample(1000).Pose == ReactionPose.Thinking, "Dot request is pending");
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
                var duringImport = store.ReadMessages();
                var importedReception = inbox.ObserveCompletedRead(duringImport.Messages, duringImport.NextCursor);
                reaction.Receive(importedReception, 1500);
                Must(importedReception.Count == 0 && reaction.WaitingForReply &&
                    reaction.Sample(1501).Pose == ReactionPose.Thinking, "Import must not animate or settle Dot");

                var reply = store.AddReply(sendId, "本物のDot返信");
                Must(store.ReadMessages(dotBefore.NextCursor).Messages.Single() == reply, "Real reply arrives exactly once");
                var completed = store.ReadMessages();
                var reception = inbox.ObserveCompletedRead(completed.Messages, completed.NextCursor);
                Must(reception.Single() == reply, "Matching Dot reply reaches the M09 observer");
                reaction.Receive(reception, 2000);
                Must(!reaction.WaitingForReply && reaction.Sample(2001).Pose == ReactionPose.Jump,
                    "Real Dot reply settles the request and animates");
                var replay = inbox.ObserveCompletedRead(completed.Messages, completed.NextCursor);
                reaction.Receive(replay, 2400);
                Must(replay.Count == 0 && reaction.Sample(2651).Pose == ReactionPose.Idle,
                    "Same completed read must not restart the reception animation");
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

    public static void DefaultMcp() => DefaultMcpAsync().GetAwaiter().GetResult();

    private static async Task DefaultMcpAsync()
    {
        using var store = new ChatStore(":memory:");
        using var webhooks = new WebhookClient();
        var handler = new McpHandler(store, webhooks);
        var fixtures = M05LocalPreview.Seed(store);
        var waiting = store.AddUserMessage(new SendMessageRequest(Guid.NewGuid().ToString("D"), "pending", null));

        async Task<JsonElement> Call(string method, object parameters)
        {
            var request = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
            return JsonSerializer.SerializeToElement(await handler.HandleAsync(request, CancellationToken.None));
        }

        var names = (await Call("tools/list", new { })).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToHashSet();
        Must(names.SetEquals(new[] { "get_context", "reply", "send_message", "import_cloud_answer" }),
            "Normal MCP advertises the new tool without dropping a legacy tool");

        async Task<JsonElement> Import(string source, string id, string body) =>
            (await Call("tools/call", new
            {
                name = "import_cloud_answer", arguments = new { source_id = source, import_id = id, text = body }
            })).GetProperty("result");

        var chat = fixtures.Single(item => item.Source.Kind == "chat").Source;
        var work = fixtures.Single(item => item.Source.Kind == "work").Source;
        foreach (var source in new[] { chat, work })
        {
            var id = Guid.NewGuid().ToString("D");
            var body = $"selected answer from {source.Kind}";
            Must(!(await Import(source.Id, id, body)).GetProperty("isError").GetBoolean(), "Explicit import accepted");
            Must(!(await Import(source.Id, id, body)).GetProperty("isError").GetBoolean(), "Identical retry accepted");
            Must((await Import(source.Id, id, body + "!")).GetProperty("isError").GetBoolean(), "Conflicting retry rejected");
            Must(store.ReadCloudAnswers(source.Id).Answers.Count(a => a.Text == body) == 1,
                "Exactly one imported row in its registered source");
        }
        Must((await Import(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "unregistered"))
            .GetProperty("isError").GetBoolean(), "Unknown registrations rejected");
        Must(store.ReadMessages().Messages.Single() == waiting && store.GetStatus().QueuedCount == 1,
            "Tool uses a separate history without settling or enqueueing a Dot message");
    }

    public static void PreviewSeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "hagoromo-m05-fixture-" + Guid.NewGuid().ToString("N"));
        var db = Path.Combine(root, "conversations.db");
        try
        {
            (CloudSource Source, CloudAnswer Answer)[] samples;
            using (var store = new ChatStore(db))
                samples = M05LocalPreview.Seed(store);
            using (var store = new ChatStore(db))
            {
                Must(samples.Length == 2 && store.ListCloudSources().Length == 2, "Two preview sources");
                Must(samples.Select(sample => sample.Source.Kind).ToHashSet().SetEquals(new[] { "chat", "work" }),
                    "Both preview kinds");
                foreach (var sample in samples)
                {
                    Must(store.ListCloudSources().Single(source => source.Id == sample.Source.Id).Title == sample.Source.Title,
                        "Preview title survived restart");
                    Must(store.ReadCloudAnswers(sample.Source.Id).Answers.Single() == sample.Answer,
                        "Fixture answer stayed in its own source");
                }
                Must(store.ReadMessages().Messages.Count == 0 && store.GetStatus().QueuedCount == 0,
                    "Preview data does not populate Dot or the outbox");
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
