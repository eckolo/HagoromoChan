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
        state.ConversationChanged(false, true, 10);
        state.SendAccepted("own-1", 30);
        state.Fail(40);
        state.Receive([Row(1, "reply", "own-1"), Row(2, "proactive-1")], 50);
        state.Receive([Row(3, "burst")], 60);
        Check(!state.WaitingForReply, "message bookkeeping is immediate despite the animation queue");
        Check(state.Sample(609).Pose == ReactionPose.Wave, "opening completes before a later event");
        Check(state.Sample(610).Pose == ReactionPose.Review, "send accepted in event order");
        Check(state.Sample(1409).Pose == ReactionPose.Review, "a failure and a new reply do not interrupt");
        Check(state.Sample(1410).Pose == ReactionPose.Failed, "failure next");
        Check(state.Sample(2210).Pose == ReactionPose.Jump, "one jump for a full batch and pending burst");
        Check(state.Sample(2860).Pose == ReactionPose.Idle, "no duplicate jump");
        state.SendAccepted("own-1", 3000);
        Check(state.Sample(3000).Pose == ReactionPose.Idle && !state.WaitingForReply, "acceptance deduplicates through completion");

        var pending = new CharacterReactionState();
        pending.Suspend(true, 0);
        pending.ConversationChanged(false, true, 10);
        pending.SendAccepted("full", 20);
        pending.Fail(30);
        pending.Receive([Row(4, "one")], 40);
        pending.Fail(50); // drop old queued greeting before dropping an event
        pending.Fail(60); // now full; skip only this incoming animation
        Check(pending.Sample(90).Pose == ReactionPose.Still, "interaction renders still");
        pending.Suspend(false, 100);
        Check(pending.Sample(100).Pose == ReactionPose.Review, "older greeting is the overflow victim");
        Check(pending.Sample(900).Pose == ReactionPose.Failed, "remaining order is stable");
        Check(pending.Sample(1700).Pose == ReactionPose.Jump, "jump remains queued");
        Check(pending.Sample(2350).Pose == ReactionPose.Failed, "fourth event retained");
        Check(pending.Sample(3150).Pose == ReactionPose.Thinking, "extra finite event was not replayed");
        pending.Stop();
        pending.SendAccepted("late", 4000);
        Check(!pending.WaitingForReply && pending.Sample(4001).Pose == ReactionPose.Still, "shutdown ignores callbacks");

        var aging = new CharacterReactionState();
        aging.Suspend(true, 0);
        aging.Receive([Row(5, "original")], 10);
        aging.Receive([Row(6, "coalesced")], 1500);
        aging.Suspend(false, 3010);
        Check(aging.Sample(3010).Pose == ReactionPose.Idle, "coalescing never extends the original queue lifetime");
    }

    public static void ConversationGestures()
    {
        var state = new CharacterReactionState();
        state.ConversationChanged(false, false, 0);
        Check(state.Sample(20).Pose == ReactionPose.Idle, "a no-op does not greet");
        state.ConversationChanged(false, true, 50);
        state.ConversationChanged(true, false, 200);
        Check(state.Sample(205).Pose == ReactionPose.Wave, "closing does not cut an active finite wave");
        state.ConversationChanged(false, true, 300, waveOnOpen: false);
        Check(state.Sample(649).Pose == ReactionPose.Wave, "opening wave still completes");
        Check(state.Sample(650).Pose == ReactionPose.Idle, "reopening discards the stale queued closing animation");

        state.SetUnread(true, 1000);
        state.ConversationChanged(true, false, 1050);
        state.ConversationChanged(true, false, 1060);
        Check(state.Sample(1749).Pose == ReactionPose.Farewell && state.Sample(1750).Pose == ReactionPose.Unread,
            "one closing animation does not acknowledge unread");
        state.SendAccepted("awaited", 1800);
        state.ConversationChanged(false, true, 1900);
        state.ObserveVisibility(false); // a non-greeting path closes it before the queued opening plays
        Check(state.Sample(2600).Pose == ReactionPose.Thinking, "passive visibility is also a stale-greeting guard");
        state.SetEnabled(false, 2650);
        state.ConversationChanged(true, false, 2700);
        Check(state.Sample(2701).Pose == ReactionPose.Still, "animation off");
        state.SetEnabled(true, 2800);
        Check(state.Sample(2800).Pose == ReactionPose.Thinking, "message state remains, queued reactions are gone");
    }

    public static void CloseDuringMessage()
    {
        foreach (var expected in new[] { ReactionPose.Jump, ReactionPose.Review, ReactionPose.Failed })
        {
            var state = new CharacterReactionState();
            if (expected == ReactionPose.Jump) state.Receive([Row(1, "incoming")], 100);
            else if (expected == ReactionPose.Review) state.SendAccepted("own", 100);
            else state.Fail(100);
            state.Suspend(true, 200);
            Check(state.Sample(250).Pose == ReactionPose.Still, "short press or menu freezes immediately");
            state.Suspend(false, 300);
            state.ConversationChanged(true, false, 300);
            var resumed = state.Sample(325);
            Check(resumed.Pose == expected && resumed.ElapsedMs == 125, "resumes the same animation frame, not the wall clock");
            Check(state.Sample(200 + resumed.DurationMs).Pose == ReactionPose.Farewell, "closure can only animate afterward");
        }

        var drag = new CharacterReactionState();
        drag.SendAccepted("own", 0);
        drag.Fail(50);
        drag.Suspend(true, 100);
        drag.Suspend(true, 125, dragging: true);
        Check(drag.Sample(200).Pose == ReactionPose.Still, "drag cancels instantly");
        drag.Suspend(false, 250);
        Check(drag.Sample(251).Pose == ReactionPose.Thinking && drag.WaitingForReply, "no stale animation or request loss after drag");

        var expired = new CharacterReactionState();
        expired.Fail(0);
        expired.Suspend(true, 100);
        expired.Suspend(false, 1101);
        Check(expired.Sample(1102).Pose == ReactionPose.Idle, "hold beyond one second does not replay");
        var disabled = new CharacterReactionState();
        disabled.Fail(0);
        disabled.Receive([Row(1, "queued")], 10);
        disabled.SetEnabled(false, 20);
        disabled.SetEnabled(true, 30);
        Check(disabled.Sample(31).Pose == ReactionPose.Idle, "motion off discards queue as well as active playback");
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
        Check(state.Sample(100) is { Pose: ReactionPose.Unread, ElapsedMs: 0 }, "restore unread without replay");
        Read(1400);
        Check(state.Sample(1470).ElapsedMs == 1370, "unchanged polling does not restart");
        history.Add(Row(2, "proactive"));
        Read(1500);
        Check(state.Sample(1501).Pose == ReactionPose.Jump, "quiet unread yields now");
        Check(state.Sample(2150) is { Pose: ReactionPose.Unread, ElapsedMs: 0 }, "fresh loop after finite reaction");
        saved.UnreadIds.Remove("old");
        state.SetUnread(saved.UnreadIds.Count > 0, 2300);
        Check(state.Sample(2350).ElapsedMs == 200, "reading one of several does not reset a loop");
        saved.UnreadIds.Clear();
        Read(2400);
        Check(state.Sample(2400).Pose == ReactionPose.Idle, "fully read returns to idle");

        var glance = new CharacterReactionState();
        glance.SetUnread(true, 0);
        var moving = glance.Sample(6000);
        Check(moving.Pose == ReactionPose.Unread && CharacterReactionState.ShowsUnreadGlance(moving.ArtworkProgress),
            "only the final second is the unread glance");
        glance.Fail(6200);
        Check(glance.Sample(6999).Pose == ReactionPose.Unread, "glance does not break early");
        Check(glance.Sample(7010).Pose == ReactionPose.Failed, "next neutral phase hands off");
        var custom = new CharacterReactionState(customArtwork: true);
        custom.SetUnread(true, 0);
        Check(custom.Sample(5000).ArtworkProgress == 0, "custom pack rests on designated neutral frame");
        custom.Fail(6250);
        Check(custom.Sample(6500).Pose == ReactionPose.Unread && custom.Sample(7000).Pose == ReactionPose.Failed,
            "the same phase policy applies to custom pack expressions");
    }

    public static void Waiting()
    {
        var state = new CharacterReactionState();
        state.SendAccepted("own-a", 0);
        state.SendAccepted("own-a", 500);
        Check(state.Sample(800) is { Pose: ReactionPose.Thinking, ElapsedMs: 0 }, "deduplicated acceptance followed by thinking");
        state.ObserveWindow(-240, 0, 900);
        Check(state.Sample(900).Pose == ReactionPose.Look, "moving desktop window can draw attention");
        state.ObserveWindow(-220, 0, 1020);
        Check(state.Sample(1150).GazeX < -.99, "updates do not restart approach");
        state.ObserveWindow(0, 0, 1200);
        Check(state.Sample(1400) is { Pose: ReactionPose.Thinking, ElapsedMs: 0 }, "target removal restores a fresh thinking cycle");
        Check(state.Sample(3150).GazeX > .25, "legacy direction-only pack has a natural fallback glance");
        state.SendAccepted("own-b", 3200);
        Check(state.Sample(3499).Pose == ReactionPose.Thinking, "active thinking tilt must reach neutral");
        Check(state.Sample(3505).Pose == ReactionPose.Review, "delayed draw hands off after the neutral boundary");
        state.Receive([Row(1, "reply-b", "own-b")], 3700);
        Check(state.WaitingForReply, "one matched reply does not settle another request");
        Check(state.Sample(4305).Pose == ReactionPose.Jump, "acknowledgment completes before arrival");
        state.Receive([Row(2, "reply-a", "own-a")], 4500);
        Check(!state.WaitingForReply, "the matching reply settles request immediately");
        Check(state.Sample(4955).Pose == ReactionPose.Jump && state.Sample(5605).Pose == ReactionPose.Idle,
            "later arrival after active jump coalesces separately");

        var gaze = new CharacterReactionState();
        gaze.Sample(0);
        gaze.ObserveCursor((1, 0));
        Check(gaze.Sample(20) is { Pose: ReactionPose.Look, ElapsedMs: 0 }, "new direct gaze has an introduction");
        gaze.ObserveCursor((0, 1));
        Check(gaze.Sample(100) is { Pose: ReactionPose.Look, ElapsedMs: 80 }, "direction updates never reset elapsed intro");
        gaze.Receive([Row(3, "finite")], 110);
        Check(gaze.Sample(199).Pose == ReactionPose.Look && gaze.Sample(200).Pose == ReactionPose.Jump,
            "protected look introduction then finite reaction");
        Check(gaze.Sample(850) is { Pose: ReactionPose.Look, ElapsedMs: 0, GazeY: 0 }, "qualified gaze restarts from neutral");
        gaze.Receive([Row(4, "finite-2")], 1040);
        Check(gaze.Sample(1040).Pose == ReactionPose.Jump, "established tracking yields immediately");
        gaze.ObserveCursor(null);
        Check(gaze.Sample(1690).Pose == ReactionPose.Idle, "unqualified gaze does not restart");

        var muted = new CharacterReactionState();
        muted.SetEnabled(false, 0);
        muted.SendAccepted("mute", 10);
        muted.Receive([Row(5, "r", "mute")], 20);
        muted.SetEnabled(true, 30);
        Check(!muted.WaitingForReply && muted.Sample(31).Pose == ReactionPose.Idle, "muted callbacks reconcile without a replay");
    }

    public static void ActionBoundariesAndShutdown()
    {
        var indexed = new ReactionTimeline(true, new(80, false, 0, .8), new(20, true, .8, 1));
        Check(!indexed.CanYieldBetween(10, 79) && indexed.CanYieldBetween(10, 120),
            "a late draw that lands in a protected phase of a new cycle still observes the preceding boundary");
        Check(indexed.CanYieldBetween(80, 80) && !indexed.CanYieldBetween(100, 179), "exact indexed boundary behavior");

        var blink = new CharacterReactionState();
        blink.Sample(0);
        var expression = blink.Sample(4660);
        Check(expression.Pose == ReactionPose.Idle && expression.ArtworkProgress >= 4650d / 4800,
            "idle expression is driven by the same indexed timeline");
        blink.Fail(4670);
        Check(blink.Sample(4799).Pose == ReactionPose.Idle && blink.Sample(4800).Pose == ReactionPose.Failed,
            "idle expression finishes before a new finite unit");

        var close = new CharacterReactionState();
        close.SendAccepted("closing", 0);
        close.Fail(100);
        close.BeginShutdown(200, animate: true);
        close.Receive([Row(1, "late", "closing")], 250);
        close.BeginShutdown(300, animate: true);
        Check(close.IsClosing && !close.WaitingForReply, "closure does not delay or discard data bookkeeping");
        Check(close.Sample(799).Pose == ReactionPose.Review, "wait only for the current protected playback");
        Check(close.Sample(800) is { Pose: ReactionPose.Shutdown, ElapsedMs: 0 }, "one dedicated farewell, not pending failure");
        close.BeginShutdown(1200, animate: true);
        Check(close.Sample(1200) is { Pose: ReactionPose.Shutdown, ElapsedMs: 400 }, "duplicate stop does not restart");
        Check(close.Sample(1500).Pose == ReactionPose.Still && close.ShutdownReady, "stop proceeds after farewell");
        var limit = new CharacterReactionState();
        limit.SetUnread(true, 0);
        limit.Sample(6000);
        limit.BeginShutdown(6001, true);
        Check(limit.Sample(6800).Pose == ReactionPose.Unread && limit.Sample(6801).Pose == ReactionPose.Shutdown,
              "normal boundary wait has a strict 800ms cap");
        Check(limit.Sample(7601).Pose == ReactionPose.Still && limit.ShutdownReady, "absolute deadline survives skipped timer ticks");

        var skip = new CharacterReactionState();
        skip.BeginShutdown(0, animate: false); // unavailable, explicitly restarting, or no visible artwork
        Check(skip.ShutdownReady && skip.Sample(1).Pose == ReactionPose.Still, "no usable farewell means immediate exit");
        var disabled = new CharacterReactionState();
        disabled.BeginShutdown(0, true);
        disabled.SetEnabled(false, 50);
        Check(disabled.ShutdownReady, "failed or disabled drawing releases shutdown immediately");
    }

    private static ChatMessage Row(long sequence, string id, string? inReplyTo = null) =>
        new(sequence, id, "assistant", "body", inReplyTo, DateTimeOffset.UnixEpoch);

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}
