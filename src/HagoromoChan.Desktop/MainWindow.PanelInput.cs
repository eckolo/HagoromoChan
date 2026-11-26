using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HagoromoChan.Desktop;

public partial class MainWindow
{
    private void WirePanelCursorGaze()
    {
        // Some controls consume input (notably the history wheel). Observe the source
        // window without claiming an event or changing focus, capture or scrolling.
        foreach (var panel in new[] { _panels.Conversation, _panels.Status })
        {
            panel.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPanelInput), true);
            panel.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPanelInput), true);
            panel.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnPanelDrag), true);
            panel.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPanelInput), true);
            panel.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(OnPanelInput), true);
            // Includes repeats while a history paging button remains held down.
            panel.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnPanelInput), true);
        }
    }

    private void OnPanelInput(object sender, RoutedEventArgs e)
    {
        // Sending has its own reaction when the relay accepts the message.
        if (e is not MouseWheelEventArgs && (e.Source == SendButton ||
            (e.OriginalSource is Visual child && SendButton.IsAncestorOf(child))))
            return;
        if (e is KeyEventArgs { Key: Key.Return } key && key.KeyboardDevice.Modifiers == ModifierKeys.Control
            && MessageInput.IsKeyboardFocusWithin && !_imeComposing && !_compositionJustEnded)
            return;
        ObservePanelCursorActivity();
    }

    private void OnPanelDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            OnPanelInput(sender, e);
    }
}
