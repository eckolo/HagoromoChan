using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

internal enum ReactionPose { Still, Idle, Look, Wave, Review, Failed, Jump }

internal readonly record struct ReactionFrame(
    ReactionPose Pose, long ElapsedMs, int DurationMs, double GazeX = 0, double GazeY = 0);

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

// Times come from one monotonic clock on the UI thread. A delayed tick skips missed frames.
internal sealed class CharacterReactionState
{
    private const int QueueLifetimeMs = 2000;
    private const int GazeTransitionMs = 200;
    private const int GazeHoldMs = 1600;
    private const int WaitingCycleMs = 4600;

    private readonly HashSet<string> _accepted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _awaitingReplies = new(StringComparer.OrdinalIgnoreCase);
    private ReactionPose? _playing;
    private long _started;
    private ReactionPose? _pending;
    private long _pendingSince;
    private long? _lastWave;
    private long _waitingSince;
    private long? _windowMoved;
    private (double X, double Y) _gazeFrom;
    private (double X, double Y) _gazeTarget;
    private bool _enabled = true;
    private bool _suspended;
    private bool _stopped;

    public bool WaitingForReply => _awaitingReplies.Count > 0;

    public void Click(long nowMs)
    {
        if (!_enabled || _stopped || _suspended || (_lastWave.HasValue && nowMs - _lastWave.Value < 800)) return;
        _lastWave = nowMs;
        Request(ReactionPose.Wave, nowMs);
    }

    public void SendAccepted(string requestId, long nowMs)
    {
        if (_stopped || !_accepted.Add(requestId)) return;
        if (!WaitingForReply)
        {
            _waitingSince = nowMs;
            _windowMoved = null;
        }
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
        _playing = null;
        _pending = null;
        _lastWave = null;
        _windowMoved = null;
        _waitingSince = nowMs;
    }

    public void Suspend(bool suspended, long nowMs)
    {
        if (_stopped || _suspended == suspended) return;
        _suspended = suspended;
        if (suspended)
        {
            _playing = null;
            _pending = null;
            _windowMoved = null;
        }
        else
            Advance(nowMs);
    }

    public void Stop()
    {
        _stopped = true;
        _playing = null;
        _pending = null;
        _awaitingReplies.Clear();
        _accepted.Clear();
    }

    // Only direction and observation time are used. Screen Y increases downwards.
    public void ObserveWindow(double x, double y, long nowMs)
    {
        if (!WaitingForReply || !_enabled || _suspended || _stopped) return;
        var distance = double.Hypot(x, y);
        if (!double.IsFinite(distance)) return;
        if (distance < 0.001)
        {
            // An explicit empty sample ends the hold now, without snapping the gaze.
            if (_windowMoved is long moved && nowMs - moved < GazeHoldMs)
            {
                _gazeTarget = GazeAt(nowMs);
                _windowMoved = nowMs - GazeHoldMs;
            }
            return;
        }
        _gazeFrom = GazeAt(nowMs);
        _gazeTarget = (x / distance, y / distance);
        _windowMoved = nowMs;
    }

    public ReactionFrame Sample(long nowMs)
    {
        if (!_enabled || _stopped || _suspended) return new(ReactionPose.Still, 0, 0);
        Advance(nowMs);
        if (_playing is { } pose)
            return new(pose, nowMs - _started, Duration(pose));

        if (WaitingForReply)
        {
            var phase = (nowMs - _waitingSince) % WaitingCycleMs;
            var watching = _windowMoved is long moved && nowMs - moved < GazeHoldMs + GazeTransitionMs;
            // Briefly relax between glances. A newly moving window always draws the gaze.
            if (watching || phase < WaitingCycleMs - 800)
            {
                var gaze = GazeAt(nowMs);
                return new(ReactionPose.Look, phase, WaitingCycleMs, gaze.X, gaze.Y);
            }
        }
        return new(ReactionPose.Idle, nowMs % 1000, 1000);
    }

    private (double X, double Y) GazeAt(long nowMs)
    {
        var cycle = (nowMs - _waitingSince) / WaitingCycleMs;
        var phase = (nowMs - _waitingSince) % WaitingCycleMs;
        // A small sideways glance once per cycle keeps a quiet desktop from looking frozen.
        var aside = phase is >= 1500 and < 2600
            ? 0.28 * Math.Sin(Math.PI * (phase - 1500) / 1100) : 0;
        var upX = cycle % 2 == 0 ? aside : -aside;
        if (_windowMoved is not long moved) return (upX, -1);
        var elapsed = nowMs - moved;
        if (elapsed < GazeTransitionMs)
        {
            var t = (double)elapsed / GazeTransitionMs;
            return (_gazeFrom.X + (_gazeTarget.X - _gazeFrom.X) * t,
                _gazeFrom.Y + (_gazeTarget.Y - _gazeFrom.Y) * t);
        }
        var back = Math.Clamp((double)(elapsed - GazeHoldMs) / GazeTransitionMs, 0, 1);
        return (_gazeTarget.X + (upX - _gazeTarget.X) * back,
            _gazeTarget.Y + (-1 - _gazeTarget.Y) * back);
    }

    private void Request(ReactionPose pose, long nowMs)
    {
        if (!_enabled || _stopped) return;
        Advance(nowMs);
        // The enum orders one-shot poses by priority: Wave < Review < Failed < Jump.
        if (!_suspended && (_playing is null || pose > _playing.Value))
        {
            _playing = pose;
            _started = nowMs;
        }
        else if ((pose is ReactionPose.Review or ReactionPose.Jump)
            && (_pending is null || pose >= _pending.Value))
        {
            _pending = pose;
            _pendingSince = nowMs;
        }
    }

    private void Advance(long nowMs)
    {
        if (_pending is not null && nowMs - _pendingSince >= QueueLifetimeMs)
            _pending = null;
        if (_playing is { } current && nowMs - _started >= Duration(current))
            _playing = null;
        if (!_suspended && _playing is null && _pending is not null)
        {
            _playing = _pending;
            _started = nowMs;
            _pending = null;
        }
    }

    private static int Duration(ReactionPose pose) => pose switch
    {
        ReactionPose.Wave => 600,
        ReactionPose.Review or ReactionPose.Failed => 800,
        ReactionPose.Jump => 650,
        _ => throw new ArgumentOutOfRangeException(nameof(pose))
    };
}
