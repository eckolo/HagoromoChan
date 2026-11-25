using HagoromoChan.Contracts;
using HagoromoChan.Desktop;

internal static class CharacterReactionTests
{
    public static void MotionPreferences()
    {
        var directory = Directory.CreateTempSubdirectory("hagoromo-motion-");
        try
        {
            var file = Path.Combine(directory.FullName, "desktop-ui.json");
            var store = new DesktopPreferencesStore(directory.FullName);
            var defaults = store.Load();
            Check(!defaults.ShouldAnimate(false) && defaults.ShouldAnimate(true), "new installation follows Windows");
            File.WriteAllText(file, """{"reactionsEnabled":false}""");
            Check(!store.Load().ShouldAnimate(true), "old explicit off remains off");
            File.WriteAllText(file, """{"reactionsEnabled":true,"screenX":71}""");
            var settings = store.Load();
            Check(!settings.ShouldAnimate(false) && settings.ShouldAnimate(true), "old on follows Windows");
            settings.FollowWindowsAnimations = false;
            store.Save(settings);
            settings = store.Load();
            Check(settings.ShouldAnimate(false) && settings.ShouldAnimate(true) && settings.ScreenX == 71,
                "app-only override survives restart and preserves other settings");
            settings.ReactionsEnabled = false;
            Check(!settings.ShouldAnimate(false) && !settings.ShouldAnimate(true), "explicit off wins");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    public static void Inbox()
    {
        var inbox = new ReactionInbox();
        var history = new List<ChatMessage> { Row(1, "old"), Row(2, "older") };
        // Entries already inserted by a failed initial read become part of the first baseline.
        Check(inbox.ObserveCompletedRead(history, 2).Count == 0, "initial history");
        history.Add(Row(3, "answer", "request"));
        history.Add(Row(4, "proactive_new"));
        history.Add(new ChatMessage(7, "ahead", "user", "", null, DateTimeOffset.UnixEpoch));
        var fresh = inbox.ObserveCompletedRead(history, 4);
        Check(fresh.Select(r => r.Id).SequenceEqual(new[] { "answer", "proactive_new" }),
            "both newly confirmed assistant IDs, including the page inserted before a retry");
        Check(inbox.ObserveCompletedRead(history, 4).Count == 0, "repeated complete read");
        history.Insert(4, Row(5, "answer"));
        history.Insert(5, Row(6, "arrived-after-post"));
        Check(inbox.ObserveCompletedRead(history, 7).Single().Id == "arrived-after-post",
            "same ID is deduplicated, earlier POST does not advance the committed cursor");

        history[^1] = Row(7, "different-history");
        history.Add(Row(8, "already-present"));
        Check(inbox.ObserveCompletedRead(history, 8).Count == 0, "replaced baseline identifier");
        Check(inbox.ObserveCompletedRead(history, 2).Count == 0, "regressed cursor resets baseline");
        Check(inbox.ObserveCompletedRead(history, 9).Count == 0, "cursor absent from the snapshot");
        Check(inbox.ObserveCompletedRead(history, 8).Count == 0, "recovery establishes a baseline");
        var empty = new ReactionInbox();
        Check(empty.ObserveCompletedRead([], 0).Count == 0, "empty initial history");
        Check(empty.ObserveCompletedRead([Row(1, "first-live")], 1).Count == 1, "first live reply");
    }

    public static void Playback()
    {
        var state = new CharacterReactionState();
        Check(state.Sample(0).Pose == ReactionPose.Idle, "initial idle");
        state.Click(10);
        Check(state.Sample(20).Pose == ReactionPose.Wave, "click");
        state.SendAccepted("own-1", 30);
        Check(state.Sample(31).Pose == ReactionPose.Review, "send preempts click");
        state.Fail(40);
        Check(state.Sample(41).Pose == ReactionPose.Failed, "definite failed operation");
        state.Receive([Row(1, "reply", "own-1"), Row(2, "proactive_1")], 50);
        Check(state.Sample(55).Pose == ReactionPose.Jump, "one jump for a complete batch");
        Check(state.Sample(700).Pose == ReactionPose.Idle, "single batch does not queue a second jump");
        state.Click(710);
        Check(state.Sample(710).Pose == ReactionPose.Idle, "repeated click cooldown");

        state.Suspend(true, 1000);
        state.Click(1020);
        state.SendAccepted("own-2", 1030);
        state.Receive([Row(3, "reply-2", "own-2")], 1040);
        Check(state.Sample(1080).Pose == ReactionPose.Still, "interaction keeps a stable frame");
        state.Suspend(false, 1100);
        Check(state.Sample(1100).Pose == ReactionPose.Jump, "highest priority pending reaction");
        Check(state.Sample(1800).Pose == ReactionPose.Idle, "a single pending slot");
        state.Suspend(true, 2000);
        state.Receive([Row(4, "proactive_2")], 2010);
        state.Suspend(false, 4100);
        Check(state.Sample(4100).Pose == ReactionPose.Idle, "expired reaction after a long drag");

        state.Receive([Row(5, "a")], 4200);
        state.Receive([Row(6, "b")], 4250);
        state.Receive([Row(7, "c")], 4300);
        Check(state.Sample(4850).Pose == ReactionPose.Jump, "burst coalesces into one more jump");
        Check(state.Sample(5500).Pose == ReactionPose.Idle, "no third jump");
        state.Stop();
        state.SendAccepted("late", 6000);
        state.Suspend(false, 6000);
        state.Receive([Row(8, "late-reply")], 6100);
        Check(!state.WaitingForReply && state.Sample(6200).Pose == ReactionPose.Still,
            "closing discards pending work and ignores later callbacks");
    }

    public static void Unread()
    {
        var state = new CharacterReactionState();
        var inbox = new ReactionInbox();
        var saved = new DesktopPreferences { ObservedSequence = 1, ObservedId = "old", UnreadIds = ["old"] };
        var history = new List<ChatMessage> { Row(1, "old") };
        void Read(long ms)
        {
            var cursor = history[^1].Sequence;
            saved.Observe(history, cursor);
            state.Receive(inbox.ObserveCompletedRead(history, cursor), ms);
            state.SetUnread(saved.UnreadIds.Count > 0, ms);
        }

        Read(100);
        var restored = state.Sample(100);
        Check(restored.Pose == ReactionPose.Unread && restored.ElapsedMs == 0 && restored.AllowsGaze,
            "first complete read restores quiet unread, without replaying arrival");
        Read(1400);
        Check(state.Sample(1470).ElapsedMs == 1370, "repeated poll preserves the unread phase");
        history.Add(Row(2, "proactive_new"));
        Read(1500);
        Check(state.Sample(1501).Pose == ReactionPose.Jump, "arrival interrupts unread");
        Check(state.Sample(2150) is { Pose: ReactionPose.Unread, ElapsedMs: 2050 },
            "additional unread ID does not restart the cycle");
        saved.UnreadIds.Remove("old");
        state.SetUnread(saved.UnreadIds.Count > 0, 2300);
        Check(state.Sample(2350).ElapsedMs == 2250, "reading one of several preserves phase");
        state.SendAccepted("own", 2500);
        Check(state.Sample(2501).Pose == ReactionPose.Review && state.Sample(3310).Pose == ReactionPose.Thinking,
            "local acceptance and waiting for a reply both take precedence");
        state.Click(3500);
        Check(state.Sample(3501).Pose == ReactionPose.Wave, "click takes precedence");
        history.Add(Row(3, "answer", "own"));
        Read(4500);
        Check(state.Sample(4501).Pose == ReactionPose.Jump && state.Sample(5150).Pose == ReactionPose.Unread,
            "matching reply settles request, then unread resumes");
        Check(!CharacterReactionState.ShowsUnreadGlance(5999d / 7000)
            && CharacterReactionState.ShowsUnreadGlance(6000d / 7000)
            && CharacterReactionState.ShowsUnreadGlance(6999d / 7000)
            && !CharacterReactionState.ShowsUnreadGlance(1),
            "only the last second is reserved for a glance at the unread surface");
        state.Suspend(true, 6800);
        Check(state.Sample(6900).Pose == ReactionPose.Still, "drag and menus suppress unread");
        state.Suspend(false, 7250);
        Check(state.Sample(7250) is { Pose: ReactionPose.Unread, ElapsedMs: 150, DurationMs: 7000 },
            "seven-second loop resumes at its continuing phase");
        saved.UnreadIds.Clear();
        state.SetUnread(false, 7300);
        Read(7350);
        Check(state.Sample(7350).Pose == ReactionPose.Idle, "read all, repeat poll, then idle");
        state.SetUnread(true, 8000);
        Check(state.Sample(8000).ElapsedMs == 0, "a separate unread period starts a new cycle");
        state.SetEnabled(false, 8100);
        Check(state.Sample(8200).Pose == ReactionPose.Still, "disabled motion stays still");
        state.SetUnread(false, 8300);
        state.SetEnabled(true, 8400);
        Check(state.Sample(8400).Pose == ReactionPose.Idle, "reading while disabled clears the pose");
    }

    public static void Waiting()
    {
        var state = new CharacterReactionState();
        state.SendAccepted("own-a", 0);
        state.SendAccepted("own-a", 500);
        var thinking = state.Sample(801);
        Check(thinking.Pose == ReactionPose.Thinking && thinking.AllowsGaze
            && thinking.DurationMs > 0, "one receipt followed by a thinking loop that a cursor may interrupt");
        state.ObserveWindow(-240, 0, 900);
        var tracking = state.Sample(1150);
        Check(tracking.Pose == ReactionPose.Look && tracking.GazeX < -0.99 && tracking.GazeY == 0,
            "moving window to the left");
        state.ObserveWindow(double.NaN, 0, 1250);
        var relaxed = state.Sample(2900);
        Check(relaxed.Pose == ReactionPose.Thinking && relaxed.ElapsedMs > thinking.ElapsedMs,
            "thinking animation resumes at a later phase when motion ends");
        Check(state.Sample(4100).Pose == ReactionPose.Thinking, "quiet wait keeps the thinking pose");
        state.ObserveWindow(300, 0, 4150);
        Check(state.Sample(4400).GazeX > 0.99, "motion draws attention during a resting interval");
        state.ObserveWindow(0, 0, 4400);
        var resumed = state.Sample(4650);
        Check(resumed.Pose == ReactionPose.Thinking && resumed.ElapsedMs == 50,
            "target disappearance returns to thinking, cycling across the phase boundary");

        state.SendAccepted("own-b", 4700);
        state.Receive([Row(1, "proactive_new")], 4800);
        var arriving = state.Sample(4801);
        Check(arriving.Pose == ReactionPose.Jump && !arriving.AllowsGaze && state.WaitingForReply,
            "proactive message takes priority over cursor and waiting pose");
        Check(state.Sample(5451).Pose == ReactionPose.Thinking,
            "unrelated proactive message finishes and thinking resumes");
        state.Receive([Row(2, "reply-b", "own-b")], 5600);
        Check(state.WaitingForReply && state.Sample(6251).Pose == ReactionPose.Thinking,
            "settling one of two requests resumes thinking for the remaining one");
        state.Receive([Row(3, "reply-a", "own-a")], 6300);
        Check(!state.WaitingForReply && state.Sample(7000).Pose == ReactionPose.Idle,
            "each corresponding reply settles its request");
        state.SendAccepted("own-a", 7200);
        Check(!state.WaitingForReply && state.Sample(7200).Pose == ReactionPose.Idle,
            "cleanup retries do not restart the send or the gaze");

        state.SetEnabled(false, 7300);
        state.SendAccepted("muted", 7350);
        state.Receive([Row(4, "reply-muted", "muted")], 7400);
        state.SetEnabled(true, 7450);
        Check(!state.WaitingForReply && state.Sample(7450).Pose == ReactionPose.Idle,
            "muted callbacks reconcile replies without replay on enable");
        state.SendAccepted("waiting-at-disable", 7500);
        state.SetEnabled(false, 7550);
        Check(state.Sample(7600).Pose == ReactionPose.Still, "disabled stays still");
        state.SetEnabled(true, 7650);
        Check(state.Sample(7650).Pose == ReactionPose.Thinking, "resume thinking without replaying acceptance");
        Check(state.Sample(9700).GazeX > 0.25, "direction-only fallback packs still glance aside during a wait");
    }

    private static ChatMessage Row(long sequence, string id, string? inReplyTo = null) =>
        new(sequence, id, "assistant", "body", inReplyTo, DateTimeOffset.UnixEpoch);

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}
