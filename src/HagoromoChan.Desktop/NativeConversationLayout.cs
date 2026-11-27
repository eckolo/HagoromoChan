using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HagoromoChan.Desktop;

// Normal WPF parent layout only: retained TextBoxes are measured at one finite
// constraint in each pass, and are arranged into the same budget. No body is
// pre-arranged full-height or manually translated behind a second viewport.
internal sealed class NativeConversationLayout : Panel
{
    private double _budget;
    private double _composerHeight;
    private double _navigationHeight;
    private double _historyHeight;
    private double _hintHeight;
    public NativeHistoryStack? History { get; set; }
    public FrameworkElement? Hint { get; set; }
    public FrameworkElement? Navigation { get; set; }
    public FrameworkElement? Composer { get; set; }

    public void SetClientBudget(double width, double height)
    {
        if (Width == width && _budget == height) return;
        Width = width;
        MaxHeight = _budget = height;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(Width, availableSize.Width);
        var budget = Math.Min(_budget, availableSize.Height);
        if (!double.IsFinite(width) || !double.IsFinite(budget)) return default;

        static double Measure(UIElement? child, double width, double height)
        {
            if (child is null || child.Visibility == Visibility.Collapsed) return 0;
            child.Measure(new Size(width, height));
            return Math.Min(height, child.DesiredSize.Height);
        }

        // Reserve the navigation bar and let the draft take its natural
        // height within the finite editor cap before offering history space.
        _navigationHeight = Measure(Navigation, width, budget);
        _composerHeight = Measure(Composer, width, budget - _navigationHeight);
        var historyBudget = budget - _navigationHeight - _composerHeight;
        _historyHeight = Measure(History, width, historyBudget);
        _hintHeight = _historyHeight == 0 ? Measure(Hint, width, historyBudget) : 0;
        return new Size(width, _historyHeight + _hintHeight + _composerHeight + _navigationHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        void Place(UIElement? child, double height)
        {
            if (child is null || child.Visibility == Visibility.Collapsed) return;
            var h = Math.Max(0, Math.Min(height, finalSize.Height - y));
            child.Arrange(new Rect(0, y, finalSize.Width, h));
            y += h;
        }

        Place(History, _historyHeight);
        if (Hint is { } hint) hint.Opacity = _hintHeight > 0 ? 1 : 0;
        Place(Hint, _hintHeight);
        Place(Composer, _composerHeight);
        Place(Navigation, _navigationHeight);
        return finalSize;
    }
}

internal sealed class NativeHistoryStack : Panel
{
    private readonly List<(UIElement Card, double Height)> _slots = [];
    private readonly HashSet<UIElement> _offered = [];
    private double _minimumCardHeight;
    private readonly Dictionary<UIElement, double> _previewMinimums = [];
    public void SetPreviewMinimums(Dictionary<UIElement, double> previewMinimums)
    {
        if (_previewMinimums.Count == previewMinimums.Count &&
            previewMinimums.All(pair => _previewMinimums.TryGetValue(pair.Key, out var height) && height == pair.Value))
            return;
        _previewMinimums.Clear();
        foreach (var (card, height) in previewMinimums) _previewMinimums.Add(card, height);
        InvalidateMeasure();
    }

    public bool Offers(UIElement element) => _offered.Contains(element);

    public void SetMinimumCardHeight(double height)
    {
        if (_minimumCardHeight == height) return;
        _minimumCardHeight = height;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _slots.Clear();
        if (!double.IsFinite(availableSize.Width) || !double.IsFinite(availableSize.Height)) return default;
        double used = 0;
        foreach (UIElement card in InternalChildren)
        {
            var remaining = availableSize.Height - used;
            if (remaining < Math.Max(_minimumCardHeight, _previewMinimums.GetValueOrDefault(card))) break;
            card.Measure(new Size(availableSize.Width, remaining));
            var height = Math.Min(remaining, card.DesiredSize.Height);
            if (height <= 0) break;
            _slots.Add((card, height));
            used += height;
        }
        return new Size(availableSize.Width, used);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        _offered.Clear();
        foreach (var (card, height) in _slots)
        {
            if (y + height > finalSize.Height) break;
            card.Clip = null;
            card.IsHitTestVisible = true;
            card.Arrange(new Rect(0, y, finalSize.Width, height));
            _offered.Add(card);
            y += height;
        }
        // Don't remeasure/arrange an out-of-budget TextBox at zero height.
        // It is never visible/hittable or offered as evidence of a read.
        foreach (UIElement card in InternalChildren)
            if (!_offered.Contains(card))
            {
                card.Clip = Geometry.Empty;
                card.IsHitTestVisible = false;
            }
        return finalSize;
    }
}
