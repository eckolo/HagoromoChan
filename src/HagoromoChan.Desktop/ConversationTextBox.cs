using System.Windows.Controls;

namespace HagoromoChan.Desktop;

internal sealed class ConversationTextBox : TextBox
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // TextBoxBase enables scrolling by its internal formatted text view
        // when it applies the template. Let the surrounding presenter move
        // both history and drafts without repopulating that view's viewport-
        // dependent line cache. Keep native selection and editable input.
        if (GetTemplateChild("PART_ContentHost") is ScrollViewer host)
            host.CanContentScroll = false;
    }
}
