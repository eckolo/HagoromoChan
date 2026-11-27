using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HagoromoChan.Desktop;

// Acts solely as a viewport: the actual read-only TextBox keeps one complete
// layout, selection, and document. Page changes move the visual, never its Text.
internal sealed class LiveHistoryViewport(TextBox body) : Decorator
{
    // Page identity is purely visual. Text is never sliced; UTF-16 offsets
    // inferred from WPF line APIs are both unnecessary and unsafe here.
    internal readonly record struct Page(int FirstBand, int BandCount, double Top, double Height);
    private IReadOnlyList<Page> _pages = [];
    private double _fullHeight;
    private double _width = -1;
    private double _scale;
    private double _maximum;
    private bool _geometryKnown;
    private bool _loadRetryUsed;
    private bool _forceFallback;

    public event Action? GeometryInvalidated;
    private string? _measured;
    private Page _current;

    public TextBox Body { get; } = body;
    public bool GeometryKnown => _geometryKnown;
    public double ViewTop => _current.Top;
    internal string? GeometryFailure { get; private set; }

    public IReadOnlyList<Page> Layout(double width, double maximum, double scale)
    {
        if (!ReferenceEquals(Child, Body))
        {
            Child = Body;
            ClipToBounds = true;
        }
        if (_measured == Body.Text && width == _width && maximum == _maximum && scale == _scale)
            return _pages;
        if (width != _width || maximum != _maximum || scale != _scale)
        {
            _loadRetryUsed = false;
            _forceFallback = false;
        }
        _measured = Body.Text;
        _width = Width = width;
        _maximum = maximum;
        _scale = scale;
        Body.Width = Body.MaxWidth = width;
        Body.ClipToBounds = false;
        Body.ApplyTemplate();
        Body.Measure(new Size(width, double.PositiveInfinity));
        _fullHeight = BubbleLayout.RoundUp(Body.DesiredSize.Height, scale);
        Body.Arrange(new Rect(0, 0, width, _fullHeight));
        var drawn = ReadLines(Body, out var failure);
        if (drawn.Count != 0)
        {
            var lastInk = drawn.Where(line => !line.Ink.IsEmpty).Select(line => line.Ink.Bottom)
                .DefaultIfEmpty(0).Max();
            if (lastInk > _fullHeight)
            {
                _fullHeight = BubbleLayout.RoundUp(lastInk, scale);
                Body.Arrange(new Rect(0, 0, width, _fullHeight));
                drawn = ReadLines(Body, out failure);
            }
        }
        var complete = _forceFallback ? null : TryPages(drawn, _fullHeight, maximum, scale);
        _geometryKnown = complete is not null;
        if (!_forceFallback)
            GeometryFailure = _geometryKnown ? null : failure ??
                $"No whole-ink page boundaries (lines={drawn.Count}, height={_fullHeight:F2}, maximum={maximum:F2}).";
        _pages = complete ?? [new(0, 0, 0, Math.Min(maximum, Math.Max(44, _fullHeight)))];
        return _pages;
    }

    public bool BeginOneGeometryRetry()
    {
        if (_geometryKnown || _forceFallback || _loadRetryUsed) return false;
        _loadRetryUsed = true;
        return true;
    }

    public void InvalidateUnverifiedLayout() => _measured = null;

    public void Select(Page page)
    {
        _current = page;
        Height = page.Height;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Body.Measure(new Size(_width > 0 ? _width : availableSize.Width,
            _geometryKnown ? double.PositiveInfinity : _current.Height));
        return new Size(Body.DesiredSize.Width, _current.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Body.Arrange(new Rect(0, -_current.Top, Math.Max(0, _width),
            _geometryKnown ? _fullHeight : arrangeSize.Height));
        // Inspect the final arrangement WPF gave the actual native child,
        // not merely the earlier full-height preparation pass.
        if (_geometryKnown && (arrangeSize.Height + 1e-6 < _current.Height ||
            Body.ActualHeight + 1e-6 < _fullHeight || Body.ActualWidth + 1e-6 < _width))
        {
            _geometryKnown = false;
            _forceFallback = true;
            GeometryFailure = $"Final viewport constrained (page={_current.Height:F2}, available={arrangeSize.Height:F2}, "
                + $"body={Body.ActualWidth:F2}x{Body.ActualHeight:F2}, expected={_width:F2}x{_fullHeight:F2}).";
            _measured = null;
            GeometryInvalidated?.Invoke();
        }
        return arrangeSize;
    }

    private readonly record struct DrawnLine(double Origin, Rect Ink);

    private static List<DrawnLine> ReadLines(TextBox body, out string? failure)
    {
        failure = null;
        // WPF attaches the native renderer as PART_ContentHost.Content. Use
        // that public relationship and its active scroll owner, not a string
        // naming a private framework type or ScrollViewer's deferred counters.
        if (body.Template?.FindName("PART_ContentHost", body) is not ScrollViewer host ||
            host.Content is not FrameworkElement view ||
            view is not IScrollInfo scroll || !ReferenceEquals(scroll.ScrollOwner, host))
        {
            failure = "No native TextBox scroll surface.";
            return [];
        }
        if (!view.IsArrangeValid || !double.IsFinite(scroll.ExtentHeight) ||
            !double.IsFinite(scroll.ViewportHeight) || scroll.ExtentHeight <= 0 ||
            scroll.ExtentHeight > scroll.ViewportHeight + 1e-6 || Math.Abs(scroll.VerticalOffset) > 1e-6 ||
            !double.IsFinite(scroll.ViewportWidth) || Math.Abs(view.RenderSize.Width - scroll.ViewportWidth) > 1e-4)
        {
            failure = $"Native layout incomplete (arranged={view.IsArrangeValid}, extent={scroll.ExtentHeight:F2}, "
                + $"viewport={scroll.ViewportWidth:F2}x{scroll.ViewportHeight:F2}, offset={scroll.VerticalOffset:F2}, "
                + $"renderWidth={view.RenderSize.Width:F2}).";
            return [];
        }
        var lines = new List<DrawnLine>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(view); i++)
        {
            if (VisualTreeHelper.GetChild(view, i) is not DrawingVisual visual)
            {
                failure = "Unexpected non-line history visual.";
                return [];
            }
            var transform = visual.TransformToAncestor(body);
            var origin = transform.Transform(new Point(0, 0)).Y;
            var bounds = VisualTreeHelper.GetDrawing(visual)?.Bounds ?? Rect.Empty;
            lines.Add(new(origin, bounds.IsEmpty ? Rect.Empty : transform.TransformBounds(bounds)));
        }
        lines.Sort((a, b) => a.Origin.CompareTo(b.Origin));
        // Require a complete sequence of visible native lines across the
        // extent, without reformatting or querying character positions.
        var firstOrigin = view.TransformToAncestor(body).Transform(new Point(0, 0)).Y;
        if (lines.Count == 0 || Math.Abs(lines[0].Origin - firstOrigin) > 1e-4)
        {
            failure = $"Missing start of line sequence (lines={lines.Count}, origin={firstOrigin:F2}).";
            return [];
        }
        var lineHeight = scroll.ExtentHeight / lines.Count;
        for (var i = 0; i < lines.Count; i++)
            if (!double.IsFinite(lines[i].Origin) ||
                Math.Abs(lines[i].Origin - firstOrigin - i * lineHeight) > 1e-4)
            {
                failure = $"Incomplete line sequence (line={i}, lines={lines.Count}, height={lineHeight:F2}).";
                return [];
            }
        return lines;
    }

    private static IReadOnlyList<Page>? TryPages(IReadOnlyList<DrawnLine> lines,
        double fullHeight, double maximum, double scale)
    {
        if (maximum <= 0 || lines.Count == 0)
            return null;
        var topSuffix = new double[lines.Count + 1];
        topSuffix[^1] = double.PositiveInfinity;
        for (var i = lines.Count - 1; i >= 0; i--)
            topSuffix[i] = Math.Min(topSuffix[i + 1], lines[i].Ink.IsEmpty ? double.PositiveInfinity : lines[i].Ink.Top);
        var cuts = new List<(int Band, double Top)> { (0, 0) };
        var precedingBottom = 0.0;
        if (topSuffix[0] < 0) return null;
        for (var i = 1; i < lines.Count; i++)
        {
            if (!lines[i - 1].Ink.IsEmpty)
                precedingBottom = Math.Max(precedingBottom, lines[i - 1].Ink.Bottom);
            // A cut between overlapping drawn lines would clip at least one
            // line. Keep such neighboring lines on the same page instead.
            var lo = BubbleLayout.RoundUp(precedingBottom, scale);
            var hi = BubbleLayout.RoundDown(topSuffix[i], scale);
            if (lo > hi) continue;
            var cut = Math.Clamp(BubbleLayout.RoundDown(lines[i].Origin, scale), lo, hi);
            if (cut > cuts[^1].Top && cut < fullHeight) cuts.Add((i, cut));
        }
        if (!lines[^1].Ink.IsEmpty) precedingBottom = Math.Max(precedingBottom, lines[^1].Ink.Bottom);
        if (precedingBottom > fullHeight) return null;
        cuts.Add((lines.Count, fullHeight));
        if (fullHeight <= maximum)
            return [new(0, lines.Count, 0, fullHeight)];

        var pages = new List<Page>();
        for (var i = 0; i < cuts.Count - 1;)
        {
            var end = i + 1;
            while (end < cuts.Count && cuts[end].Top - cuts[i].Top <= maximum) end++;
            end--;
            if (end == i) return null;
            pages.Add(new(cuts[i].Band, cuts[end].Band - cuts[i].Band,
                cuts[i].Top, cuts[end].Top - cuts[i].Top));
            i = end;
        }
        return pages;
    }
}
