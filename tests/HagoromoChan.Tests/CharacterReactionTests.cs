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

    public static void Waiting()
    {
        var state = new CharacterReactionState();
        state.SendAccepted("own-a", 0);
        state.SendAccepted("own-a", 500);
        var lookup = state.Sample(801);
        Check(lookup.Pose == ReactionPose.Look && lookup.GazeX == 0 && lookup.GazeY < 0,
            "one receipt followed by an upward gaze");
        state.ObserveWindow(-240, 0, 900);
        var tracking = state.Sample(1150);
        Check(tracking.Pose == ReactionPose.Look && tracking.GazeX < -0.99 && tracking.GazeY == 0,
            "moving window to the left");
        state.ObserveWindow(double.NaN, 0, 1250);
        var relaxed = state.Sample(2900);
        Check(relaxed.Pose == ReactionPose.Look && relaxed.GazeY == -1, "stale window direction fades");
        Check(state.Sample(4100).Pose == ReactionPose.Idle, "break between waiting glances");
        state.ObserveWindow(300, 0, 4150);
        Check(state.Sample(4400).GazeX > 0.99, "motion draws attention during a resting interval");
        state.ObserveWindow(0, 0, 4400);
        Check(state.Sample(4650).GazeY == -1, "target disappearance releases gaze without a second hold");

        state.SendAccepted("own-b", 4700);
        state.Receive([Row(1, "proactive_new")], 4800);
        Check(state.Sample(4801).Pose == ReactionPose.Jump && state.WaitingForReply,
            "proactive messages react without settling an unrelated request");
        state.Receive([Row(2, "reply-b", "own-b")], 5600);
        Check(state.WaitingForReply, "a second request remains open");
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
        Check(state.Sample(7650).Pose == ReactionPose.Look, "resume waiting without replaying acceptance");
        Check(state.Sample(9700).GazeX > 0.25, "quiet desktop allows a brief upward side glance");
    }

    private static ChatMessage Row(long sequence, string id, string? inReplyTo = null) =>
        new(sequence, id, "assistant", "body", inReplyTo, DateTimeOffset.UnixEpoch);

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}
