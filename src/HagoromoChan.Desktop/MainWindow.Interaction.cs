using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

public partial class MainWindow : Window
{
    private bool _farewellAfterMenu;
    private bool ConversationVisible => _panels.ConversationOpen && _panels.Conversation.IsVisible;

    private void WireDesktopEvents()
    {
        _placementTimer.Tick += (_, _) => CheckPlacement();
        WirePanelCursorGaze();
        MascotSurface.PreviewMouseLeftButtonDown += BeginPointer;
        MascotSurface.PreviewMouseMove += MovePointer;
        MascotSurface.PreviewMouseLeftButtonUp += EndPointer;
        MascotSurface.LostMouseCapture += (_, _) =>
        {
            if (!_pointerDown)
                return;
            var moved = _dragging;
            _pointerDown = false;
            _dragging = false;
            RefreshReactionSuspension();
            if (moved)
                RememberPosition();
        };
        MascotSurface.PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenSettings();
        };
    }

    private void BeginPointer(object sender, MouseButtonEventArgs e)
    {
        if (!_initialized || _pointerDown || _exitRequested)
            return;
        _dragStart = NativeDesktop.Cursor();
        _dragAnchor = NativeDesktop.Bounds(this);
        _pointerDown = Mouse.Capture(MascotSurface);
        _dragging = false;
        RefreshReactionSuspension();
        e.Handled = true;
    }

    private void MovePointer(object sender, MouseEventArgs e)
    {
        if (!_pointerDown || e.LeftButton != MouseButtonState.Pressed)
            return;
        var cursor = NativeDesktop.Cursor();
        var dx = cursor.X - _dragStart.X;
        var dy = cursor.Y - _dragStart.Y;
        if (!_dragging)
        {
            var scale = NativeDesktop.Scale(this);
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * scale
                && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * scale)
                return;
            _dragging = true;
            RefreshReactionSuspension();
        }

        var size = NativeDesktop.Bounds(this);
        var area = NativeDesktop.WorkArea(cursor.X, cursor.Y);
        var target = OverlayLayout.Clamp(area, new ScreenRect(_dragAnchor.X + dx, _dragAnchor.Y + dy,
            size.Width, size.Height));
        NativeDesktop.Move(this, target.X, target.Y);
        CheckPlacement();
        e.Handled = true;
    }

    private void EndPointer(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown)
            return;
        var moved = _dragging;
        var clicked = !moved && MascotSurface.IsMouseOver;
        _pointerDown = false;
        _dragging = false;
        if (Mouse.Captured == MascotSurface)
            Mouse.Capture(null);
        RefreshReactionSuspension();
        e.Handled = true;
        if (moved)
            RememberPosition();
        else if (clicked)
        {
            if (_panels.ConversationOpen)
                DismissSurfaces();
            else
                OpenBoth();
        }
    }

    private void RestorePosition(bool reset = false)
    {
        // Restore in physical pixels, then constrain to the currently available monitor.
        if (!reset && _preferences.ScreenX is int x && _preferences.ScreenY is int y)
            NativeDesktop.Move(this, x, y);
        else
        {
            var area = NativeDesktop.PrimaryWorkArea();
            var size = NativeDesktop.Bounds(this);
            var margin = (int)Math.Ceiling(16 * NativeDesktop.Scale(this));
            NativeDesktop.Move(this, Math.Max(area.X, area.Right - size.Width - margin),
                Math.Max(area.Y, area.Bottom - size.Height - margin));
        }
        CheckPlacement();
    }

    private void CheckPlacement()
    {
        if (_closing || _exitRequested)
            return;
        var area = NativeDesktop.WorkArea(this);
        var scale = NativeDesktop.Scale(this);
        var pixels = OverlayLayout.MascotPixels(_assetPixelWidth, _assetPixelHeight,
            _preferences.Height, scale, area);
        var height = pixels.Height / scale;
        var width = pixels.Width / scale;
        if (Math.Abs(Height - height) > .001 || Math.Abs(Width - width) > .001)
        {
            Height = height;
            Width = width;
            UpdateLayout();
        }
        var current = NativeDesktop.Bounds(this);
        var clamped = OverlayLayout.Clamp(area, current);
        NativeDesktop.Move(this, clamped.X, clamped.Y);
        current = NativeDesktop.Bounds(this);
        if (_lastBounds != current || _lastArea != area || Math.Abs(_lastScale - scale) > .001)
        {
            _lastBounds = current;
            _lastArea = area;
            _lastScale = scale;
            _reactionArtwork.SetDisplayScale(Math.Min((double)current.Width / _assetPixelWidth,
                (double)current.Height / _assetPixelHeight));
            RestingSilhouette.Source = _reactionArtwork.RestingImage;
            if (!_reactionTimer.IsEnabled)
                MascotImage.Source = RestingSilhouette.Source;
            _panels.Arrange();
        }
    }

    private void RememberPosition()
    {
        var bounds = NativeDesktop.Bounds(this);
        _preferences.ScreenX = bounds.X;
        _preferences.ScreenY = bounds.Y;
        SavePreferences();
    }

    private void SavePreferences()
    {
        if (_preferencesSaveBlocked)
            return;
        try
        {
            _preferencesStore.Save(_preferences);
            _preferencesWarning = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _preferencesWarning = "表示設定を保存できません。 " + error.Message;
        }
        RefreshStatus();
    }

    private void ChangeConversationByUser(Action change, bool waveOnOpen = false)
    {
        var wasVisible = ConversationVisible;
        change();
        var visible = ConversationVisible;
        if (wasVisible == visible || _closing || _exitRequested || _controlRequest) return;

        if (!visible && _contextMenu is { IsOpen: true })
        {
            // A menu item can run before Closed releases the reaction suspension.
            _farewellAfterMenu = true;
            return;
        }

        _farewellAfterMenu = false;
        _reactions.ConversationChanged(wasVisible, visible, Environment.TickCount64, waveOnOpen);
    }

    private void DismissSurfaces()
    {
        if (!_initialized || _closing)
            return;
        _compactSuppressed = true;
        _expandedStatus = false;
        if (_contextMenu is { IsOpen: true } menu)
            menu.IsOpen = false;
        ChangeConversationByUser(_panels.HideSurfaces);
    }

    private bool PrepareConversation()
    {
        if (!_initialized || _closing) return false;
        _compactSuppressed = false;
        RefreshHistory();
        return true;
    }

    private void OpenBoth()
    {
        if (!PrepareConversation()) return;
        ChangeConversationByUser(_panels.OpenBoth, waveOnOpen: true);
        RefreshStatus();
    }

    private void OpenComposer()
    {
        if (!PrepareConversation()) return;
        ChangeConversationByUser(_panels.FocusInput);
        RefreshStatus();
    }

    private void ToggleComposer()
    {
        if (!PrepareConversation()) return;
        ChangeConversationByUser(_panels.ToggleInput);
        RefreshStatus();
    }

    private void ToggleHistory()
    {
        if (!PrepareConversation()) return;
        ChangeConversationByUser(_panels.ToggleHistory);
        RefreshStatus();
    }

    private void OpenSettings()
    {
        if (!_initialized || _closing || _exitRequested)
            return;
        var menu = new ContextMenu
        {
            PlacementTarget = MascotSurface,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };
        _contextMenu = menu;
        menu.Items.Add(ToggleMenuCommand("話しかける", _panels.ComposerOpen, ToggleComposer));
        menu.Items.Add(ToggleMenuCommand("会話履歴", _panels.HistoryOpen, ToggleHistory));
        // On very small work areas a status surface may have no safe nearby slot.
        // Keep its existing diagnostic and retry actions reachable without moving it away.
        if (_panels.StatusNeedsAlternateAccess)
        {
            menu.Items.Add(new MenuItem { Header = ConnectionText.Text, IsEnabled = false });
            menu.Items.Add(MenuCommand("状態の詳細をコピー", _panels.CopyStatus));
            if (RelayRetryButton.Visibility == Visibility.Visible && RelayRetryButton.IsEnabled)
                menu.Items.Add(MenuCommand("配信を再試行",
                    () => RelayRetryButton_Click(RelayRetryButton, new RoutedEventArgs())));
            if (_panels.UnreadButton.Visibility == Visibility.Visible)
                menu.Items.Add(MenuCommand("未読の発言を表示", OpenUnread));
        }
        menu.Items.Add(new Separator());
        var top = new MenuItem { Header = "最前面表示", IsCheckable = true, IsChecked = _preferences.Topmost };
        top.Click += (_, _) =>
        {
            _preferences.Topmost = top.IsChecked;
            Topmost = top.IsChecked;
            _panels.SetTopmost(top.IsChecked);
            SavePreferences();
        };
        menu.Items.Add(top);
        var motion = new MenuItem { Header = "キャラクターの動き" };
        var systemState = SystemParameters.ClientAreaAnimation ? "オン" : "オフ";
        var enabled = _preferences.ReactionsEnabled;
        var follow = _preferences.FollowWindowsAnimations;
        motion.Items.Add(ToggleMenuCommand($"Windowsに合わせる（現在は{systemState}）", enabled && follow,
            () => SetReactionPreference(enabled: true, followWindows: true)));
        motion.Items.Add(ToggleMenuCommand("常に動かす", enabled && !follow,
            () => SetReactionPreference(enabled: true, followWindows: false)));
        motion.Items.Add(ToggleMenuCommand("動かさない", !enabled,
            () => SetReactionPreference(enabled: false, followWindows: true)));
        menu.Items.Add(motion);
        var size = new MenuItem { Header = "表示サイズ" };
        foreach (var (label, height) in new[] { ("小（120）", 120), ("中（180）", 180), ("大・標準（260）", 260) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _preferences.Height == height };
            item.Click += (_, _) =>
            {
                _preferences.Height = height;
                CheckPlacement();
                RememberPosition();
            };
            size.Items.Add(item);
        }
        menu.Items.Add(size);
        menu.Items.Add(MenuCommand("表示位置を初期化", () =>
        {
            RestorePosition(reset: true);
            RememberPosition();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(ToggleMenuCommand("接続状態", _expandedStatus, ToggleConnectionDetails));
        if (!string.IsNullOrEmpty(_managerPipe))
            menu.Items.Add(MenuCommand("再起動", () => RequestManagedControl(RuntimeCommand.Restart)));
        menu.Items.Add(MenuCommand("終了", RequestExit));
        menu.Closed += (_, _) =>
        {
            _contextMenu = null;
            RefreshReactionSuspension();
            if (_farewellAfterMenu)
            {
                _farewellAfterMenu = false;
                if (!_closing && !_exitRequested && !_controlRequest)
                    _reactions.ConversationChanged(true, ConversationVisible, Environment.TickCount64);
            }
        };
        menu.IsOpen = true;
        RefreshReactionSuspension();
    }

    private void SetReactionPreference(bool enabled, bool followWindows)
    {
        _preferences.ReactionsEnabled = enabled;
        _preferences.FollowWindowsAnimations = followWindows;
        ApplyReactionSettings();
        SavePreferences();
    }

    private static MenuItem ToggleMenuCommand(string label, bool open, Action action)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = open };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem MenuCommand(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }
}
