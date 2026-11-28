using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace HagoromoChan.Desktop;

public partial class MainWindow
{
    private readonly ReactionInbox _reactionInbox = new();
    private readonly CharacterReactionState _reactions;
    private readonly WindowActivityGaze _windowGaze = new();
    private readonly CursorGaze _cursorGaze = new();
    private readonly CharacterReactionArtwork _reactionArtwork;
    private readonly DispatcherTimer _reactionTimer = new(DispatcherPriority.Background);
    private string? _reactionRequestId;
    private bool _animationUnavailable;
    private bool _gazeSampling;
    private bool _reactionsClosed;
    private bool _mascotFirstFrame;

    private void ObserveInitialMascotFrame()
    {
        if (!_initialized || !_mascotFirstFrame) return;
        _reactions.ObserveStartup(_initialized, _mascotFirstFrame, Environment.TickCount64);
        RenderReactions(this, EventArgs.Empty);
    }

    private void StartReactions()
    {
        RestingSilhouette.Source = MascotImage.Source;
        _reactionTimer.Tick += RenderReactions;
        SystemParameters.StaticPropertyChanged += ReactionSystemSettingChanged;
        ApplyReactionSettings();
    }

    private void ReactionSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.InvokeAsync(ApplyReactionSettings);
    }

    private bool AnimationsEnabled => !_animationUnavailable && _reactionArtwork.CanAnimate
        && _preferences.ShouldAnimate(SystemParameters.ClientAreaAnimation);

    private void ApplyReactionSettings()
    {
        if (_reactionsClosed || !_initialized) return;
        _reactions.SetEnabled(AnimationsEnabled, Environment.TickCount64);
        if (_localExitQueued)
        {
            if (_reactions.ShutdownReady)
                FinishLocalExit();
            return;
        }
        RefreshReactionSuspension();
    }

    private void RefreshReactionSuspension()
    {
        if (!_initialized || _reactionsClosed || _localExitQueued) return;
        var suspended = _pointerDown;
        _reactions.Suspend(suspended, Environment.TickCount64, dragging: _dragging);
        if (!AnimationsEnabled || suspended)
        {
            _reactionTimer.Stop();
            ResetWindowGaze();
            _cursorGaze.Reset();
            // With a custom pack its resting frame also owns the baseline size/pose.
            if (AnimationsEnabled)
                RenderReactions(this, EventArgs.Empty);
            else
            {
                MascotImage.Source = RestingSilhouette.Source;
            }
            return;
        }
        RenderReactions(this, EventArgs.Empty);
        if (!_animationUnavailable)
            _reactionTimer.Start();
    }

    private void ResetWindowGaze()
    {
        if (!_gazeSampling) return;
        _windowGaze.Reset();
        _gazeSampling = false;
    }

    private void ObservePanelCursorActivity()
    {
        if (_reactionTimer.IsEnabled)
            _cursorGaze.NotePanelInteraction(Environment.TickCount64);
    }

    private void NotifyLocalAcceptance()
    {
        if (!_pendingAccepted || _reactionRequestId is null
            || !string.Equals(_pending?.RequestId, _reactionRequestId, StringComparison.OrdinalIgnoreCase))
            return;
        _reactions.SendAccepted(_reactionRequestId, Environment.TickCount64);
        _reactionRequestId = null;
    }

    private void RenderReactions(object? sender, EventArgs e)
    {
        if (_reactionsClosed) return;
        try
        {
            var now = Environment.TickCount64;
            (double X, double Y)? target = null;
            (double X, double Y)? cursorDirection = null;
            var waiting = _reactions.WaitingForReply;
            if (!_localExitQueued && AnimationsEnabled && !_pointerDown && _contextMenu is not { IsOpen: true }
                && _lastBounds is { } mascot)
            {
                var cursor = NativeDesktop.TryCursor();
                if (cursor is { } at)
                    cursorDirection = _cursorGaze.Sample(mascot, _lastScale, at.X, at.Y, now);
                if (_historyLoaded && (waiting || !_connectionProblem))
                {
                    _gazeSampling = true;
                    target = _windowGaze.Sample(mascot);
                }
                else ResetWindowGaze();
            }
            else ResetWindowGaze();

            // Observations are demand, never an override of the selected frame.
            _reactions.ObserveCursor(cursorDirection ?? (waiting ? null : target));
            if (waiting)
                _reactions.ObserveWindow(target?.X ?? 0, target?.Y ?? 0, now);

            var frame = _reactions.Sample(now);
            if (_localExitQueued && _reactions.ShutdownReady)
            {
                FinishLocalExit();
                return;
            }

            var direction = (X: frame.GazeX, Y: frame.GazeY);
            var progress = frame.ArtworkProgress;
            if (frame.LooksAtUnread
                && _lastBounds is { } bounds)
                direction = _panels.UnreadDirection(bounds);

            var image = _reactionArtwork.Frame(frame.Artwork, progress, direction.X, direction.Y, frame.ArtworkCell);
            if (!ReferenceEquals(image, MascotImage.Source)) MascotImage.Source = image;
            var interval = frame.AllowsGaze ? 100 : 50;
            if (_reactionTimer.Interval.TotalMilliseconds != interval)
                _reactionTimer.Interval = TimeSpan.FromMilliseconds(interval);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or COMException or NotSupportedException or System.IO.IOException)
        {
            Trace.TraceWarning("Character animation disabled: {0}", error.GetType().Name);
            _animationUnavailable = true;
            _reactionTimer.Stop();
            _reactions.SetEnabled(false, Environment.TickCount64);
            ResetWindowGaze();
            _cursorGaze.Reset();
            MascotImage.Source = RestingSilhouette.Source;
            if (_localExitQueued)
                FinishLocalExit();
        }
    }

    private void StopReactions()
    {
        if (_reactionsClosed) return;
        _reactionsClosed = true;
        _reactionTimer.Stop();
        _reactionTimer.Tick -= RenderReactions;
        SystemParameters.StaticPropertyChanged -= ReactionSystemSettingChanged;
        ResetWindowGaze();
        _cursorGaze.Reset();
        _reactions.Stop();
    }
}
