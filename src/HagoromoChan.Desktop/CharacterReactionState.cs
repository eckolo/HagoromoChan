using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

internal enum ReactionPose { Still, Idle, Unread, Thinking, Look, Farewell, Wave, Review, Failed, Jump, Shutdown }

internal readonly record struct ReactionFrame(ReactionPose Pose, long ElapsedMs, int DurationMs,
    double GazeX = 0, double GazeY = 0, bool AllowsGaze = false, double ArtworkProgress = 0,
    bool DirectGaze = false);

// ReadMessagesAsync passes the sequence-ordered desktop history after its last page succeeds.
// History may already contain rows from an earlier, incomplete read or a POST beyond the cursor.
internal sealed class ReactionInbox
{
    private readonly HashSet<string> _seenAssistantIds = new(StringComparer.Ordinal);
    private long? _cursor;
    private string? _cursorId;

    public IReadOnlyList<ChatMessage> ObserveCompletedRead(IReadOnlyList<ChatMessage> snapshot, long cursor)
    {
        var end = CountThrough(snapshot, cursor);
        if (cursor < 0 || (cursor > 0 && (end == 0 || snapshot[end - 1].Sequence != cursor)))
        {
            _cursor = null;
            return Array.Empty<ChatMessage>();
        }

        var prior = _cursor.GetValueOrDefault();
        var start = CountThrough(snapshot, prior);
        var continuous = _cursor.HasValue && prior <= cursor && (prior == 0 ||
            (start > 0 && snapshot[start - 1].Sequence == prior && snapshot[start - 1].Id == _cursorId));
        if (!continuous)
        {
            _seenAssistantIds.Clear();
            start = 0;
        }

        List<ChatMessage>? fresh = null;
        for (var index = start; index < end; index++)
        {
            var row = snapshot[index];
            if (row.Role == "assistant" && _seenAssistantIds.Add(row.Id) && continuous)
                (fresh ??= []).Add(row);
        }

        _cursor = cursor;
        _cursorId = end > 0 ? snapshot[end - 1].Id : null;
        return fresh is null ? Array.Empty<ChatMessage>() : fresh;
    }

    private static int CountThrough(IReadOnlyList<ChatMessage> snapshot, long sequence)
    {
        var lo = 0;
        var hi = snapshot.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (snapshot[mid].Sequence <= sequence)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}

// Phase boundaries, not pose-specific interruption branches, govern all
// playback. Artwork may stretch/compress within each part without changing
// the monotonic timeout or the scheduler's designated rest boundaries.
internal sealed class ReactionTimeline(bool repeat, params ReactionTimeline.Part[] parts)
{
    internal readonly record struct Part(int Ms, bool CanYield, double From, double To);
    public int Duration { get; } = parts.Sum(part => part.Ms);

    private (Part Part, long Position, long End) Locate(long elapsed)
    {
        var bounded = Math.Max(0, elapsed);
        var at = repeat ? bounded % Duration : Math.Min(bounded, Duration - 1);
        long start = 0;
        foreach (var part in parts)
        {
            var end = start + part.Ms;
            if (at < end)
                return (part, at - start, bounded >= Duration && !repeat ? long.MaxValue : bounded + end - at);
            start = end;
        }
        throw new InvalidOperationException("Reaction timeline has no active phase.");
    }

    public long PhaseTime(long elapsed) => repeat ? elapsed % Duration : Math.Min(elapsed, Duration);

    public double Progress(long elapsed)
    {
        if (!repeat && elapsed >= Duration)
            return parts[^1].To;
        var (part, position, _) = Locate(elapsed);
        return part.From + (part.To - part.From) * position / part.Ms;
    }

    // A delayed render can cross a valid hand-off boundary and land inside the
    // next protected phase. Do not require a tick to hit the exact boundary.
    public bool CanYieldBetween(long from, long to)
    {
        if (to < from) return false;
        if (!parts.Any(part => part.CanYield)) return false;
        if (repeat && to - from >= Duration) return true;
        for (var time = from; time <= to;)
        {
            var (part, _, end) = Locate(time);
            if (part.CanYield) return true;
            if (end <= time || end > to) return false;
            time = end;
        }
        return false;
    }
}

// All times come from the same monotonic UI clock. Input/data bookkeeping
// never wait on an animation deadline.
internal sealed class CharacterReactionState(bool customArtwork = false)
{
    private const int QueueLifetimeMs = 3000;
    private const int MaxPending = 4;
    private const int HoldLimitMs = 1000;
    private const int GazeIntroMs = 180;
    private const int WindowGazeHoldMs = 1600;
    private const int WaitingCycleMs = 4600;
    internal const int UnreadCycleMs = 7000;
    private const int ClosingWaitMs = 800;
    internal const int ClosingDeadlineMs = 1600;

    private enum Aim { None, Wait, Cursor }
    private enum Suspension { None, Hold, Drag }
    private sealed record ActionSpec(ReactionTimeline Timeline, Aim Aim = Aim.None,
        bool? Visible = null, bool Coalesce = false);
    private readonly record struct Queued(ReactionPose Pose, long At);
    private readonly record struct Playing(ReactionPose Pose, long Started);

    private static ReactionTimeline Clip(int ms) => new(false, new ReactionTimeline.Part(ms, false, 0, 1));

    private static readonly IReadOnlyDictionary<ReactionPose, ActionSpec> Specs =
        new Dictionary<ReactionPose, ActionSpec>
        {
            [ReactionPose.Still] = new(new ReactionTimeline(true, new ReactionTimeline.Part(1000, true, 0, 0))),
            [ReactionPose.Idle] = new(new ReactionTimeline(true,
                new(4650, true, 0, 4650d / 4800), new(150, false, 4650d / 4800, 1))),
            [ReactionPose.Thinking] = new(new ReactionTimeline(true,
                new(1400, true, 0, 4d / 48), new(700, false, 4d / 48, 44d / 48),
                new(2500, true, 44d / 48, 1)), Aim.Wait),
            [ReactionPose.Unread] = new(new ReactionTimeline(true,
                new(6000, true, 0, 6000d / UnreadCycleMs), new(1000, false, 6000d / UnreadCycleMs, 1))),
            [ReactionPose.Look] = new(new ReactionTimeline(false,
                new(GazeIntroMs, false, 0, 1), new(1, true, 1, 1)), Aim.Cursor),
            [ReactionPose.Wave] = new(Clip(600), Visible: true),
            [ReactionPose.Farewell] = new(Clip(700), Visible: false),
            [ReactionPose.Review] = new(Clip(800)),
            [ReactionPose.Failed] = new(Clip(800)),
            [ReactionPose.Jump] = new(Clip(650), Coalesce: true),
            [ReactionPose.Shutdown] = new(Clip(700))
        };

    // A custom pack may not identify neutral internal frames. Use a static
    // frame while yielding, and protect only short, complete artwork units.
    private static readonly IReadOnlyDictionary<ReactionPose, ReactionTimeline> Custom =
        new Dictionary<ReactionPose, ReactionTimeline>
        {
            [ReactionPose.Idle] = new(true, new(800, true, 0, 0), new(200, false, 0, 1)),
            [ReactionPose.Thinking] = new(true, new(1400, true, 0, 0),
                new(700, false, 0, 1), new(2500, true, 0, 0)),
            [ReactionPose.Unread] = new(true, new(6000, true, 0, 0), new(1000, false, 0, 1))
        };

    private readonly HashSet<string> _accepted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _awaitingReplies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Queued> _pending = [];
    private Playing? _finite, _background;
    private ReactionPose _wanted = ReactionPose.Idle;
    private long _wantedSince;
    private ReactionPose? _lastGreeting;
    private long _lastGreetingAt;
    private bool _unread, _visible;
    private long? _windowMoved;
    private (double X, double Y) _windowTarget;
    private (double X, double Y)? _cursor;
    private (double X, double Y) _lastLookTarget;
    private bool _lastLookDirect;
    private long _suspendedAt;
    private Suspension _suspension;
    private bool _enabled = true, _stopped;
    private long? _closingAt;
    private bool _farewellStarted;

    public bool WaitingForReply => _awaitingReplies.Count > 0;
    public bool ShutdownReady { get; private set; }
    public bool IsClosing => _closingAt.HasValue;

    internal static bool ShowsUnreadGlance(double progress) =>
        progress >= (UnreadCycleMs - 1000d) / UnreadCycleMs && progress < 1;

    private ReactionTimeline Timeline(ReactionPose pose) =>
        customArtwork && Custom.TryGetValue(pose, out var alternate) ? alternate : Specs[pose].Timeline;

    public void SetUnread(bool hasUnread, long nowMs)
    {
        if (_stopped) return;
        _unread = hasUnread;
        Advance(nowMs);
    }

    public void ObserveVisibility(bool visible) => _visible = visible;

    public void ConversationChanged(bool wasVisible, bool visible, long nowMs, bool waveOnOpen = true)
    {
        if (_stopped || wasVisible == visible) return;
        _visible = visible;
        var pose = visible ? ReactionPose.Wave : ReactionPose.Farewell;
        if (visible && !waveOnOpen)
        {
            _lastGreeting = null;
            Advance(nowMs);
            return;
        }
        if (!_enabled || IsClosing || (_lastGreeting == pose && nowMs - _lastGreetingAt < 800))
        {
            Advance(nowMs);
            return;
        }
        _lastGreeting = pose;
        _lastGreetingAt = nowMs;
        Request(pose, nowMs);
    }

    public void SendAccepted(string requestId, long nowMs)
    {
        if (_stopped || !_accepted.Add(requestId)) return;
        _awaitingReplies.Add(requestId);
        Request(ReactionPose.Review, nowMs);
    }

    public void Receive(IReadOnlyList<ChatMessage> newAssistantRows, long nowMs)
    {
        if (_stopped || newAssistantRows.Count == 0) return;
        foreach (var row in newAssistantRows)
            if (row.InReplyTo is not null)
                _awaitingReplies.Remove(row.InReplyTo);
        Request(ReactionPose.Jump, nowMs);
    }

    public void Fail(long nowMs) => Request(ReactionPose.Failed, nowMs);

    public void SetEnabled(bool enabled, long nowMs)
    {
        if (_stopped || _enabled == enabled) return;
        _enabled = enabled;
        ClearVisuals();
        _lastGreeting = null;
        if (!enabled && IsClosing) ShutdownReady = true;
        Advance(nowMs);
    }

    public void Suspend(bool suspended, long nowMs, bool dragging = false)
    {
        if (_stopped || IsClosing) return;
        var next = !suspended ? Suspension.None : dragging ? Suspension.Drag : Suspension.Hold;
        if (_suspension == next) return;
        if (_suspension == Suspension.None)
        {
            Advance(nowMs);
            _suspendedAt = nowMs;
        }

        if (next == Suspension.Drag)
        {
            _finite = null;
            _pending.Clear();
        }
        else if (next == Suspension.None && _finite is { } held)
        {
            var duration = nowMs - _suspendedAt;
            _finite = _suspension == Suspension.Hold && duration <= HoldLimitMs
                ? held with { Started = held.Started + duration } : null;
        }
        _background = null;
        _cursor = null;
        _windowMoved = null;
        _suspension = next;
        Advance(nowMs);
    }

    public void BeginShutdown(long nowMs, bool animate)
    {
        if (IsClosing || _stopped) return;
        _closingAt = nowMs;
        _pending.Clear();
        if (_suspension != Suspension.None) _finite = null;
        _suspension = Suspension.None;
        if (!animate || !_enabled)
        {
            ShutdownReady = true;
            return;
        }
        Advance(nowMs);
    }

    public void Stop()
    {
        _stopped = true;
        ClearVisuals();
        _awaitingReplies.Clear();
        _accepted.Clear();
        _unread = false;
        if (IsClosing) ShutdownReady = true;
    }

    private void ClearVisuals()
    {
        _pending.Clear();
        _finite = _background = null;
        _cursor = null;
        _windowMoved = null;
    }

    public void ObserveCursor((double X, double Y)? direction)
    {
        if (_stopped || IsClosing) return;
        _cursor = direction is { } target ? Normalize(target.X, target.Y) : null;
    }

    public void ObserveWindow(double x, double y, long nowMs)
    {
        if (!WaitingForReply || !_enabled || _suspension != Suspension.None || _stopped || IsClosing) return;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        if (Normalize(x, y) is { } target)
        {
            _windowTarget = target;
            _windowMoved = nowMs;
        }
        else if (_windowMoved is long moved && nowMs - moved < WindowGazeHoldMs)
            _windowMoved = nowMs - WindowGazeHoldMs;
    }

    private static (double X, double Y)? Normalize(double x, double y)
    {
        var length = double.Hypot(x, y);
        return double.IsFinite(length) && length >= .001 ? (x / length, y / length) : null;
    }

    public ReactionFrame Sample(long nowMs)
    {
        Advance(nowMs);
        if (!_enabled || _stopped || ShutdownReady || _suspension != Suspension.None)
            return Frame(new Playing(ReactionPose.Still, nowMs), nowMs, false);

        if (_finite is { } finite)
            return Frame(finite, nowMs, false);
        return Frame(_background ?? new Playing(ReactionPose.Idle, nowMs), nowMs, true);
    }

    private ReactionFrame Frame(Playing play, long now, bool background)
    {
        var span = Timeline(play.Pose);
        var elapsed = Math.Max(0, now - play.Started);
        var phase = span.PhaseTime(elapsed);
        var aim = Specs[play.Pose].Aim;
        var gaze = aim switch
        {
            Aim.Cursor => CursorDirection(elapsed),
            Aim.Wait => WaitingDirection(elapsed),
            _ => (0.0, 0.0)
        };
        return new(play.Pose, phase, span.Duration, gaze.Item1, gaze.Item2, background,
            span.Progress(elapsed), aim == Aim.Cursor && _lastLookDirect);
    }

    private (double, double) CursorDirection(long elapsed)
    {
        var t = Math.Clamp((double)elapsed / GazeIntroMs, 0, 1);
        var (x, y) = _lastLookTarget;
        // Direct cursor tracking starts at neutral, even while waiting for a reply.
        // Only a passive window glance inherits the upward thinking direction.
        var fromY = !_lastLookDirect && WaitingForReply ? -1 : 0;
        return (x * t, y * t + fromY * (1 - t));
    }

    private static (double, double) WaitingDirection(long elapsed)
    {
        var phase = elapsed % WaitingCycleMs;
        var aside = phase is >= 1400 and < 2100
            ? .28 * Math.Sin(Math.PI * (phase - 1400) / 700) : 0;
        return (elapsed / WaitingCycleMs % 2 == 0 ? aside : -aside, -1);
    }

    private ReactionPose Demand(long nowMs)
    {
        if (_cursor is { } cursor)
        {
            _lastLookTarget = cursor;
            _lastLookDirect = true;
            return ReactionPose.Look;
        }
        if (WaitingForReply && _windowMoved is long moved && nowMs - moved < WindowGazeHoldMs + GazeIntroMs)
        {
            var fade = 1 - Math.Clamp((double)(nowMs - moved - WindowGazeHoldMs) / GazeIntroMs, 0, 1);
            _lastLookTarget = (_windowTarget.X * fade, _windowTarget.Y * fade - (1 - fade));
            _lastLookDirect = false;
            return ReactionPose.Look;
        }
        if (WaitingForReply) return ReactionPose.Thinking;
        return _unread ? ReactionPose.Unread : ReactionPose.Idle;
    }

    private void Request(ReactionPose pose, long nowMs)
    {
        if (!_enabled || _stopped || IsClosing) return;
        Prune(nowMs);
        var spec = Specs[pose];
        if (spec.Coalesce && _pending.Any(item => item.Pose == pose)) return;
        if (_pending.Count >= MaxPending)
        {
            var greeting = _pending.FindIndex(item => Specs[item.Pose].Visible.HasValue);
            if (greeting < 0) return;
            _pending.RemoveAt(greeting);
        }
        _pending.Add(new Queued(pose, nowMs));
        Advance(nowMs);
    }

    private void Prune(long nowMs) => _pending.RemoveAll(item =>
        nowMs - item.At >= QueueLifetimeMs || Specs[item.Pose].Visible is bool desired && desired != _visible);

    private void Advance(long nowMs)
    {
        if (_stopped || !_enabled || ShutdownReady) return;
        if (IsClosing)
        {
            AdvanceShutdown(nowMs);
            return;
        }
        Prune(nowMs);
        if (_suspension != Suspension.None) return;
        if (_finite is { } current && nowMs - current.Started < Timeline(current.Pose).Duration)
            return;
        _finite = null;

        var demand = Demand(nowMs);
        if (demand != _wanted)
        {
            _wanted = demand;
            _wantedSince = nowMs;
        }
        if (_pending.Count != 0)
        {
            var leading = _pending[0];
            if (!CanLeaveBackground(leading.At, nowMs)) return;
            _pending.RemoveAt(0);
            _finite = new Playing(leading.Pose, nowMs);
            _background = null;
        }
        else if (_background is null ||
            _background.Value.Pose != demand && CanLeaveBackground(_wantedSince, nowMs))
            _background = new Playing(demand, nowMs);
    }

    private bool CanLeaveBackground(long wantedAt, long nowMs) =>
        _background is not { } baseAction || Timeline(baseAction.Pose).CanYieldBetween(
            Math.Max(0, wantedAt - baseAction.Started), nowMs - baseAction.Started);

    private void AdvanceShutdown(long nowMs)
    {
        var requested = _closingAt!.Value;
        if (nowMs - requested >= ClosingDeadlineMs)
        {
            ShutdownReady = true;
            return;
        }
        if (_farewellStarted)
        {
            if (_finite is not { } farewell || nowMs - farewell.Started >= Timeline(farewell.Pose).Duration)
                ShutdownReady = true;
            return;
        }
        if (nowMs - requested < ClosingWaitMs &&
            ((_finite is { } current && nowMs - current.Started < Timeline(current.Pose).Duration) ||
             !CanLeaveBackground(requested, nowMs)))
            return;

        _background = null;
        _finite = new Playing(ReactionPose.Shutdown, nowMs);
        _farewellStarted = true;
    }
}
