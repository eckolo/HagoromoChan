using System.Windows.Controls;

namespace HagoromoChan.Desktop;

internal sealed class HistoryTextBox : TextBox
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // TextBoxBase enables scrolling by its internal formatted text view
        // when it applies the template. Move history through the surrounding
        // presenter instead, so scrolling does not repopulate that view's
        // viewport-dependent line cache. Keep the same native text editor.
        if (GetTemplateChild("PART_ContentHost") is ScrollViewer host)
            host.CanContentScroll = false;
    }
}
