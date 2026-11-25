using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HagoromoChan.Desktop;

public partial class MainWindow
{
    private readonly ReactionInbox _reactionInbox = new();
    private readonly CharacterReactionState _reactions = new();
    private readonly WindowActivityGaze _windowGaze = new();
    private readonly CharacterReactionArtwork _reactionArtwork;
    private readonly DispatcherTimer _reactionTimer = new(DispatcherPriority.Background);
    private string? _reactionRequestId;
    private long? _hoverSince;
    private bool _animationUnavailable;
    private bool _gazeSampling;
    private bool _reactionsClosed;

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
        && _preferences.ReactionsEnabled && SystemParameters.ClientAreaAnimation;

    private void ApplyReactionSettings()
    {
        if (_reactionsClosed || !_initialized) return;
        _reactions.SetEnabled(AnimationsEnabled, Environment.TickCount64);
        RefreshReactionSuspension();
    }

    private void RefreshReactionSuspension()
    {
        if (!_initialized || _reactionsClosed) return;
        var suspended = _pointerDown || _contextMenu is { IsOpen: true };
        _reactions.Suspend(suspended, Environment.TickCount64);
        if (!AnimationsEnabled || suspended)
        {
            _reactionTimer.Stop();
            ResetGaze();
            // With a custom pack its resting frame also owns the baseline size/pose.
            if (AnimationsEnabled)
                RenderReactions(this, EventArgs.Empty);
            else
                MascotImage.Source = RestingSilhouette.Source;
            return;
        }
        RenderReactions(this, EventArgs.Empty);
        if (!_animationUnavailable)
            _reactionTimer.Start();
    }

    private void ResetGaze()
    {
        if (!_gazeSampling) return;
        _windowGaze.Reset();
        _gazeSampling = false;
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
            var frame = _reactions.Sample(now);
            (double X, double Y)? target = null;
            if (_historyLoaded && frame.Pose != ReactionPose.Still && _lastBounds is { } bounds
                && (_reactions.WaitingForReply || !_connectionProblem))
            {
                _gazeSampling = true;
                target = _windowGaze.Sample(bounds);
            }
            else
                ResetGaze();

            if (_reactions.WaitingForReply)
            {
                _reactions.ObserveWindow(target?.X ?? 0, target?.Y ?? 0, now);
                frame = _reactions.Sample(now);
            }
            var direction = (X: frame.GazeX, Y: frame.GazeY);
            var action = frame.Pose switch
            {
                ReactionPose.Idle => "idle",
                ReactionPose.Wave => "waving",
                ReactionPose.Review => "review",
                ReactionPose.Jump => "jumping",
                ReactionPose.Failed => "failed",
                ReactionPose.Look => "waiting",
                _ => "frozen"
            };
            if (frame.Pose == ReactionPose.Idle && !_reactions.WaitingForReply)
            {
                if (_hoverSince is long since && now - since >= 300 && MascotSurface.IsMouseOver)
                {
                    var at = Mouse.GetPosition(MascotSurface);
                    var dx = at.X - MascotSurface.ActualWidth * .5;
                    var dy = at.Y - MascotSurface.ActualHeight * .25;
                    var distance = Math.Max(1, double.Hypot(dx, dy));
                    direction = (dx / distance, dy / distance);
                    action = "look";
                }
                else if (target is { } active)
                {
                    direction = active;
                    action = "look";
                }
            }

            var scale = VisualTreeHelper.GetDpi(MascotImage);
            var factor = Math.Min(MascotImage.ActualWidth * scale.DpiScaleX / _assetPixelWidth,
                MascotImage.ActualHeight * scale.DpiScaleY / _assetPixelHeight);
            if (factor > 0) _reactionArtwork.SetDisplayScale(factor);
            var progress = frame.DurationMs > 0 ? (double)frame.ElapsedMs / frame.DurationMs : 0;
            var image = _reactionArtwork.Frame(action, progress, direction.X, direction.Y);
            if (!ReferenceEquals(image, MascotImage.Source)) MascotImage.Source = image;
            var interval = frame.Pose is ReactionPose.Idle or ReactionPose.Look ? 100 : 50;
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
            ResetGaze();
            MascotImage.Source = RestingSilhouette.Source;
        }
    }

    private void StopReactions()
    {
        if (_reactionsClosed) return;
        _reactionsClosed = true;
        _reactionTimer.Stop();
        _reactionTimer.Tick -= RenderReactions;
        SystemParameters.StaticPropertyChanged -= ReactionSystemSettingChanged;
        ResetGaze();
        _reactions.Stop();
    }
}
