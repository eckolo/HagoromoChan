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
    private void WireDesktopEvents()
    {
        MouseEnter += (_, _) => EnterMascot();
        MouseLeave += (_, _) => LeaveHoverRegion();
        _panels.Hover.MouseEnter += (_, _) => _hoverOut.Stop();
        _panels.Hover.MouseLeave += (_, _) => LeaveHoverRegion();
        _hoverIn.Tick += (_, _) =>
        {
            _hoverIn.Stop();
            if (_initialized && !_pointerDown && _contextMenu is null && IsMouseOver)
                _panels.SetVisible(_panels.Hover, true);
        };
        _hoverOut.Tick += (_, _) =>
        {
            _hoverOut.Stop();
            if (!IsMouseOver && !_panels.Hover.IsMouseOver)
                _panels.SetVisible(_panels.Hover, false);
        };
        _placementTimer.Tick += (_, _) => CheckPlacement();
        MascotImage.PreviewMouseLeftButtonDown += BeginPointer;
        MascotImage.PreviewMouseMove += MovePointer;
        MascotImage.PreviewMouseLeftButtonUp += EndPointer;
        MascotImage.LostMouseCapture += (_, _) =>
        {
            if (!_pointerDown)
                return;
            var moved = _dragging;
            _pointerDown = false;
            _dragging = false;
            if (moved)
                RememberPosition();
        };
        MascotImage.PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenSettings();
        };
    }

    private void EnterMascot()
    {
        _hoverOut.Stop();
        if (_initialized && !_pointerDown && _contextMenu is null && !_panels.Wants(_panels.Hover))
        {
            _hoverIn.Stop();
            _hoverIn.Start();
        }
    }

    private void LeaveHoverRegion()
    {
        _hoverIn.Stop();
        _hoverOut.Stop();
        _hoverOut.Start();
    }

    private void HideHover()
    {
        _hoverIn.Stop();
        _hoverOut.Stop();
        _panels.SetVisible(_panels.Hover, false);
    }

    private void BeginPointer(object sender, MouseButtonEventArgs e)
    {
        if (!_initialized || _pointerDown)
            return;
        _dragStart = NativeDesktop.Cursor();
        _dragAnchor = NativeDesktop.Bounds(this);
        _pointerDown = Mouse.Capture(MascotImage);
        _dragging = false;
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
            HideHover();
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
        var clicked = !moved && MascotImage.IsMouseOver;
        _pointerDown = false;
        _dragging = false;
        if (Mouse.Captured == MascotImage)
            Mouse.Capture(null);
        e.Handled = true;
        if (moved)
            RememberPosition();
        else if (clicked)
            OpenComposer();
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
        if (_closing)
            return;
        var area = NativeDesktop.WorkArea(this);
        var scale = NativeDesktop.Scale(this);
        var height = Math.Max(24, Math.Min(_preferences.Height,
            Math.Min(area.Height / scale - 16, (area.Width / scale - 16) / _assetRatio)));
        if (Math.Abs(Height - height) > .1)
        {
            Height = height;
            Width = height * _assetRatio;
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

    private void OpenComposer()
    {
        if (!_initialized || _closing)
            return;
        HideHover();
        if (!_panels.Wants(_panels.Bubble))
        {
            var latest = Answers().LastOrDefault();
            if (latest is not null)
                ShowReply(latest.Id);
        }
        _panels.FocusInput();
        RefreshStatus();
    }

    private void OpenHistory()
    {
        if (!_initialized || _closing)
            return;
        HideHover();
        _panels.FocusHistory();
        if (!_historyOpened)
        {
            _historyOpened = true;
            ScrollToLatest();
        }
        RefreshStatus();
    }

    private void OpenSettings()
    {
        if (!_initialized || _closing)
            return;
        HideHover();
        var menu = new ContextMenu
        {
            PlacementTarget = MascotImage,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };
        _contextMenu = menu;
        menu.Items.Add(MenuCommand("会話", OpenComposer));
        menu.Items.Add(MenuCommand("会話履歴", OpenHistory));
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
        var size = new MenuItem { Header = "表示サイズ" };
        foreach (var (label, height) in new[] { ("小（120）", 120), ("標準（180）", 180), ("大（260）", 260) })
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
        menu.Items.Add(MenuCommand("終了", Close));
        menu.Closed += (_, _) =>
        {
            _contextMenu = null;
            if (!_closing && IsMouseOver)
                EnterMascot();
        };
        menu.IsOpen = true;
    }

    private static MenuItem MenuCommand(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }
}
